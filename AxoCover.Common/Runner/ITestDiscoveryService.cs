﻿using AxoCover.Common.Models;
using System.ServiceModel;

namespace AxoCover.Common.Runner
{
  [ServiceContract(
    SessionMode = SessionMode.Required,
    CallbackContract = typeof(ITestDiscoveryMonitor))]
  public interface ITestDiscoveryService : ITestService
  {
    [OperationContract]
    TestCase[] DiscoverTests(TestDiscoveryTask[] discoveryTasks, string runSettingsPath);
  }
}
