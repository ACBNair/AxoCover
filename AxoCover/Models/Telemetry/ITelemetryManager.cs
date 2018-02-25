using AxoCover.Common.Models;
using System.Threading.Tasks;

namespace AxoCover.Models.Telemetry
{
  public interface ITelemetryManager
  {
    bool IsTelemetryEnabled { get; set; }

    Task<bool> UploadExceptionAsync(SerializableException exception, bool force = false);
  }
}
