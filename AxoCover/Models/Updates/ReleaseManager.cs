using AxoCover.Common.Extensions;
using AxoCover.Models.Editor;
using AxoCover.Models.Extensions;
using AxoCover.Models.Telemetry;
using AxoCover.Properties;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AxoCover.Models.Updates
{
  public class ReleaseManager : IReleaseManager
  {
    private const string _defaultBranch = "release";
    private const string _userName = "axodox";
    private const string _repositoryName = "AxoCover";
    private const string _assetName = "AxoCover.vsix";
    private readonly TimeSpan _updateInterval = TimeSpan.FromDays(1);
    private readonly Regex _nameRegex = new Regex(@"^(?<branch>.*?)-(?<version>\d+(?:\.\d+)*)$");
    private readonly Regex _propertyRegex = new Regex(@"#(?<name>\w+):""(?<value>(?:[^""]|(?<=\\)"")*)""");
    private readonly IEditorContext _editorContext;
    private readonly ITelemetryManager _telemetryManager;
    private readonly Mutex _installerMutex = new Mutex(false, "AxoCover.ReleaseManager");

    public string DefaultBranch
    {
      get { return _defaultBranch; }
    }

    public bool IsUpdatingAutomatically
    {
      get { return Settings.Default.IsUpdatingAutomatically; }
      set { Settings.Default.IsUpdatingAutomatically = value; }
    }

    public DateTime LastUpdateCheckTime
    {
      get { return Settings.Default.ReleaseListUpdateTime; }
      private set { Settings.Default.ReleaseListUpdateTime = value; }
    }

    public Release[] Releases
    {
      get { return JsonConvert.DeserializeObject<Release[]>(Settings.Default.ReleaseListCache); }
      private set { Settings.Default.ReleaseListCache = JsonConvert.SerializeObject(value); }
    }

    public string TargetBranch
    {
      get { return Settings.Default.ReleaseBranch; }
      set { Settings.Default.ReleaseBranch = value; }
    }

    public Version CurrentVersion
    {
      get { return Settings.Default.ReleaseInstalled; }
      private set { Settings.Default.ReleaseInstalled = value; }
    }

    public Version[] PreviousVersions
    {
      get { return JsonConvert.DeserializeObject<Version[]>(Settings.Default.ReleaseRollbackList); }
      private set { Settings.Default.ReleaseRollbackList = JsonConvert.SerializeObject(value); }
    }

    public async Task<Release[]> GetReleases(bool isCaching = true)
    {
      var releases = isCaching ? Releases : new Release[0];
      if (DateTime.Now - LastUpdateCheckTime > _updateInterval || !isCaching)
      {
        try
        {
          using (var webClient = new WebClient())
          {
            webClient.Headers.Add(HttpRequestHeader.UserAgent, _repositoryName + "ReleaseManager");
            var result = await webClient.DownloadStringTaskAsync(new Uri($"https://api.github.com/repos/{_userName}/{_repositoryName}/releases"));

            var releaseList = new List<Release>();
            var jsonReleases = JsonConvert.DeserializeObject(result);
            foreach (JObject jsonRelease in jsonReleases as JArray)
            {
              var jsonName = jsonRelease["name"] as JValue;
              var nameMatch = _nameRegex.Match(jsonName.Value<string>());

              if (!nameMatch.Success) continue;
              var branch = nameMatch.Groups["branch"].Value;
              var versionString = nameMatch.Groups["version"].Value;
              while (versionString.Count(p => p == '.') < 3)
              {
                versionString += ".0";
              }
              var version = Version.Parse(versionString);

              var jsonCreatedAt = jsonRelease["created_at"] as JValue;
              var createdAt = DateTime.Parse(jsonCreatedAt.Value<string>());

              var jsonAssets = jsonRelease["assets"] as JArray;
              var jsonAsset = jsonAssets
                .OfType<JObject>()
                .FirstOrDefault(p => p["name"].Value<string>() == _assetName);

              var uri = jsonAsset["browser_download_url"].Value<string>();

              var jsonDescription = jsonRelease["body"] as JValue;
              var description = jsonDescription.Value<string>();

              var properties = _propertyRegex
                .Matches(description)
                .OfType<Match>()
                .ToDictionary(p => p.Groups["name"].Value, p => p.Groups["value"].Value);

              var release = new Release(branch, version, uri, createdAt, description, properties);
              releaseList.Add(release);
            }

            Releases = releases = releaseList.ToArray();
            LastUpdateCheckTime = DateTime.Now;
          }
        }
        catch
        {
          if (!isCaching)
          {
            return new Release[0];
          }
        }
      }

      return releases;
    }

    public async Task<Release> GetTargetRelease(bool isCaching = true)
    {
      var releases = await GetReleases(isCaching);

      //Get latest release on target branch
      var targetRelease = releases.GetLatest(TargetBranch);

      //If target branch has been merged switch to that branch instead
      while (targetRelease != null && targetRelease.MergedTo != null)
      {
        TargetBranch = targetRelease.MergedTo;
        targetRelease = releases.GetLatest(targetRelease.MergedTo);
      }

      //If we found no release switch to default branch
      if (targetRelease == null)
      {
        targetRelease = releases.GetLatest(_defaultBranch);
        if (targetRelease != null)
        {
          TargetBranch = _defaultBranch;
        }
      }

      return targetRelease;
    }

    public async Task<bool> TryInstallRelease(Release release)
    {
      try
      {
        _installerMutex.WaitOne();
        var downloadPath = Path.Combine(Path.GetTempPath(), "AxoCover." + release.Version + ".vsix");

        try
        {
          if (File.Exists(downloadPath))
          {
            File.Delete(downloadPath);
          }
        }
        catch
        {
          _editorContext.WriteToLog($"Cannot write to {downloadPath}. Maybe another installation is in progress?");
          return false;
        }

        using (var webClient = new WebClient())
        {
          await webClient.DownloadFileTaskAsync(release.AlternativeUri ?? release.Uri, downloadPath);
        }

        return await Task.Run(async () =>
        {
          try
          {
            InstallVsix(downloadPath);
            return true;
          }
          catch (Exception e)
          {
            await _telemetryManager.UploadExceptionAsync(e);
            return false;
          }
        });
      }
      catch
      {
        return false;
      }
      finally
      {
        _installerMutex.ReleaseMutex();
      }
    }

    private void InstallVsix(string downloadPath)
    {
      //Reflect on extension manager, so we can install beta builds without meddling with VS2017's crippled extension installer
      var extensionManagerType = Type.GetType("Microsoft.VisualStudio.ExtensionManager.SVsExtensionManager, Microsoft.VisualStudio.ExtensionManager");

      var extensionManagerInterface = Type.GetType("Microsoft.VisualStudio.ExtensionManager.IVsExtensionManager, Microsoft.VisualStudio.ExtensionManager");
      var getInstalledExtensionsMethod = extensionManagerInterface.GetMethod("GetInstalledExtensions");
      var createInstallableExtensionMethod = extensionManagerInterface.GetMethod("CreateInstallableExtension");
      var getInstalledExtensionMethod = extensionManagerInterface.GetMethod("GetInstalledExtension");
      var disableMethod = extensionManagerInterface.GetMethod("Disable");
      var enableMethod = extensionManagerInterface.GetMethod("Enable");
      var uninstallMethod = extensionManagerInterface.GetMethod("Uninstall");
      var installMethod = extensionManagerInterface.GetMethod("Install");
            
      var extensionInterface = Type.GetType("Microsoft.VisualStudio.ExtensionManager.IExtension, Microsoft.VisualStudio.ExtensionManager");
      var headerProperty = extensionInterface.GetProperty("Header");
      var nameProperty = headerProperty.PropertyType.GetProperty("Name");
      var identifierProperty = headerProperty.PropertyType.GetProperty("Identifier");
     
      //Try to resolve extension manager
      var extensionManager = Microsoft.VisualStudio.Shell.Package.GetGlobalService(extensionManagerType);
      if (extensionManager != null)
      {
        var oldExtension = (getInstalledExtensionsMethod.Invoke(extensionManager, new object[0]) as IEnumerable<object>)
          .Where(p => nameProperty.GetValue(headerProperty.GetValue(p)) as string == "AxoCover").SingleOrDefault();
        var newExtension = createInstallableExtensionMethod.Invoke(extensionManager, new object[] { downloadPath });

        //Disable current extension
        disableMethod.Invoke(extensionManager, new[] { oldExtension });

        //Uninstall current extension
        try
        {
          uninstallMethod.Invoke(extensionManager, new[] { oldExtension });
        }
        catch
        {
          enableMethod.Invoke(extensionManager, new[] { oldExtension });
          throw;
        }

        //Install new extension
        installMethod.Invoke(extensionManager, new[] { newExtension, false });

        //Enable new extension
        var newlyInstalledVersion = getInstalledExtensionMethod.Invoke(extensionManager, new[] { identifierProperty.GetValue(headerProperty.GetValue(newExtension)) });
        if (newlyInstalledVersion != null)
        {
          enableMethod.Invoke(extensionManager, new[] { newlyInstalledVersion });
        }
      }
      //If extension manager is missing try traditional VSIX installer
      else
      {
        var vsixInstallerPath = Path.Combine(_editorContext.RootPath, "VSIXInstaller.exe");

        if (Process.Start(vsixInstallerPath, $"/quiet /uninstall:{AxoCoverPackage.Id}").WaitForExitAndGetResult() != 0)
        {
          throw new Exception("VSIX uninstaller failed.");
        }

        if (Process.Start(vsixInstallerPath, $"/quiet \"{downloadPath}\"").WaitForExitAndGetResult() != 0)
        {
          throw new Exception("VSIX installer failed.");
        }
      }
    }

    public ReleaseManager(IEditorContext editorContext, ITelemetryManager telemetryManager)
    {
      _editorContext = editorContext;
      _telemetryManager = telemetryManager;
      Task.Run(Initialize);
    }

    private async Task Initialize()
    {
      var version = Assembly.GetExecutingAssembly().GetName().Version;

      //After installing a new version update release history
      if (version != CurrentVersion)
      {
        CurrentVersion = version;

        var previousReleases = PreviousVersions.ToList();
        previousReleases.Remove(version);
        previousReleases.Insert(0, version);
        PreviousVersions = previousReleases.ToArray();
      }

      //Initialize target branch for updates
      if (string.IsNullOrEmpty(TargetBranch))
      {
        //Find current release
        var releases = await GetReleases();
        var currentRelease = releases.FirstOrDefault(p => p.Version == version);

        //If we found the current release, then do auto updating
        if (currentRelease != null)
        {
          TargetBranch = currentRelease.Branch;
          IsUpdatingAutomatically = true;
        }
        //Otherwise set target branch to default, and do not enable auto updating
        else
        {
          TargetBranch = _defaultBranch;
        }
      }
    }
  }
}
