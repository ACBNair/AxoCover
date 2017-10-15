﻿using AxoCover.Common.Extensions;
using AxoCover.Common.Models;
using AxoCover.Common.ProcessHost;
using AxoCover.Common.Runner;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using System.Threading;

namespace AxoCover.Runner
{
  class Program
  {
    private static ManualResetEvent _isFinished = new ManualResetEvent(false);
    private static TimeSpan _closeTimeout = TimeSpan.FromSeconds(2);

    public static void Exit()
    {
      _isFinished.Set();
    }

    private static void Main(string[] args)
    {
      bool debugMode = false;

      try
      {
        const int argumentCount = 4;
        RunnerMode runnerMode;
        int parentPid;
        CommunicationProtocol protocol;        
        string[] assemblyPaths;
        
        if (args.Length < argumentCount ||
          !Enum.TryParse(args[0], true, out runnerMode) ||
          !int.TryParse(args[1], out parentPid) ||
          !Enum.TryParse(args[2], true, out protocol) ||
          !bool.TryParse(args[3], out debugMode) ||
          !args.Skip(argumentCount).All(p => File.Exists(p)))
        {
          throw new Exception("Arguments are invalid.");
        }

        assemblyPaths = args.Skip(argumentCount).ToArray();

        if (debugMode)
        {
          AppDomain.CurrentDomain.FirstChanceException += (o, e) => Console.WriteLine(e.Exception.GetDescription().PadLinesLeft("   "));
        }

        RunTestService(runnerMode, parentPid, protocol, assemblyPaths);
      }
      catch (Exception e)
      {
        if (debugMode)
        {
          if (Debugger.IsAttached)
          {
            Debugger.Break();
          }
          else
          {
            Debugger.Launch();
          }
        }

        var crashFilePath = Path.GetTempFileName();
        var crashDetails = JsonConvert.SerializeObject(new SerializableException(e));
        File.WriteAllText(crashFilePath, crashDetails);
        ServiceProcess.PrintServiceFailed(crashFilePath);
      }
    }

    private static void RunTestService(RunnerMode runnerMode, int parentPid, CommunicationProtocol protocol, string[] assemblyPaths)
    {
      AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

      foreach (var assemblyPath in assemblyPaths)
      {
        Console.Write($"Loading {assemblyPath}... ");
        Assembly.LoadFrom(assemblyPath);
        Console.WriteLine("Done.");
      }

      Process parentProcess = null;
      try
      {
        parentProcess = Process.GetProcessById(parentPid);
        parentProcess.EnableRaisingEvents = true;
        parentProcess.Exited += OnParentProcessExited;
      }
      catch (Exception e)
      {
        throw new Exception("Cannot open parent process.", e);
      }

      Type serviceInterface;
      Type serviceImplementation;
      GetService(runnerMode, out serviceInterface, out serviceImplementation);

      Console.WriteLine("AxoCover.Runner");
      Console.WriteLine("Copyright (c) 2016-2017 Péter Major");
      Console.WriteLine();

      Console.WriteLine($"Starting {runnerMode} service...");
      var serviceAddress = NetworkingExtensions.GetServiceAddress(protocol);
      var serviceBinding = NetworkingExtensions.GetServiceBinding(protocol);

      var serviceHost = new ServiceHost(serviceImplementation, new[] { serviceAddress });
      serviceHost.AddServiceEndpoint(serviceInterface, serviceBinding, serviceAddress);
      serviceHost.Open();
      ServiceProcess.PrintServiceStarted(serviceAddress);

      _isFinished.WaitOne();
      Console.WriteLine("Exiting...");
      try
      {
        serviceHost.Close(_closeTimeout);
      }
      catch { }

      //Make sure to kill leftover non-background threads started by tests
      Environment.Exit(0);
    }

    private static void GetService(RunnerMode runnerMode, out Type serviceInterface, out Type serviceImplementation)
    {
      switch (runnerMode)
      {
        case RunnerMode.Discovery:
          serviceInterface = typeof(ITestDiscoveryService);
          serviceImplementation = typeof(TestDiscoveryService);
          break;
        case RunnerMode.Execution:
          serviceInterface = typeof(ITestExecutionService);
          serviceImplementation = typeof(TestExecutionService);
          break;
        default:
          throw new Exception("Invalid mode of usage specified!");
      }
    }

    private static void OnParentProcessExited(object sender, EventArgs e)
    {
      Console.WriteLine("Parent exited, runner will quit too.");
      Environment.Exit(0);
    }

    private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
    {
      var assemblyName = new AssemblyName(args.Name).Name;
      var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
      return loadedAssemblies.FirstOrDefault(p => p.GetName().Name == assemblyName);
    }
  }
}
