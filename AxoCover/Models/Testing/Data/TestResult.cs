using System;

namespace AxoCover.Models.Testing.Data
{
  public class TestResult : ITestResult
  {
    public TestMethod Method { get; set; }

    public TimeSpan Duration { get; set; }

    public TestState Outcome { get; set; }

    public string ErrorMessage { get; set; }

    public StackItem[] StackTrace { get; set; }

    public int SessionId { get; set; }

    public string Output { get; set; }
  }
}
