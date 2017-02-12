﻿using AxoCover.Common.Events;
using AxoCover.Common.Extensions;
using AxoCover.Common.ProcessHost;
using AxoCover.Common.Runner;
using AxoCover.Models.Extensions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceModel;
using System.Threading;

namespace AxoCover.Models
{
  public class ExecutionProcess : ServiceProcess, ITestExecutionMonitor
  {
    private readonly ManualResetEvent _serviceStartedEvent = new ManualResetEvent(false);

    private ITestExecutionService _testExecutionService;

    public event EventHandler<EventArgs<string>> MessageReceived;
    public event EventHandler<EventArgs<TestCase>> TestStarted;
    public event EventHandler<EventArgs<TestCase>> TestEnded;
    public event EventHandler<EventArgs<TestResult>> TestResult;

    private ExecutionProcess() :
      base("AxoCover.Runner.exe", RunnerMode.Execution + " " + Process.GetCurrentProcess().Id)
    {
      _serviceStartedEvent.WaitOne();
    }

    public static ExecutionProcess Create()
    {
      var ExecutionProcess = new ExecutionProcess();

      if (ExecutionProcess._testExecutionService == null)
      {
        throw new Exception("Could not create service.");
      }
      else
      {
        return ExecutionProcess;
      }
    }

    protected override void OnServiceStarted(Uri address)
    {
      var channelFactory = new DuplexChannelFactory<ITestExecutionService>(this, NetworkingExtensions.GetServiceBinding());
      _testExecutionService = channelFactory.CreateChannel(new EndpointAddress(address));
      _testExecutionService.Initialize();

      var adapters = AdapterExtensions.GetAdapters();
      foreach (var adapter in adapters)
      {
        _testExecutionService.TryLoadAdaptersFromAssembly(adapter);
      }

      _serviceStartedEvent.Set();
    }

    protected override void OnServiceFailed()
    {
      _serviceStartedEvent.Set();
    }

    void ITestExecutionMonitor.SendMessage(TestMessageLevel testMessageLevel, string message)
    {
      var text = testMessageLevel.GetShortName() + " " + message;
      MessageReceived?.Invoke(this, new EventArgs<string>(text));
    }

    void ITestExecutionMonitor.RecordStart(TestCase testCase)
    {
      TestStarted?.Invoke(this, new EventArgs<TestCase>(testCase));
    }

    void ITestExecutionMonitor.RecordEnd(TestCase testCase, TestOutcome outcome)
    {
      TestEnded?.Invoke(this, new EventArgs<TestCase>(testCase));
    }

    void ITestExecutionMonitor.RecordResult(TestResult testResult)
    {
      TestResult?.Invoke(this, new EventArgs<TestResult>(testResult));
    }

    public void RunTests(IEnumerable<TestCase> testCases, string runSettingsPath)
    {
      _testExecutionService.RunTests(testCases, runSettingsPath);
    }
  }
}
