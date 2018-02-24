using AxoCover.Models.Testing.Data.CoverageReport;
using System.Collections.Generic;
using System.Linq;

namespace AxoCover.Models.Testing.Data
{
  public class TestReport
  {
    public TestResult[] TestResults { get; private set; }

    public CoverageSession CoverageReport { get; private set; }

    public TestReport(IEnumerable<TestResult> testResults, CoverageSession coverageReport)
    {
      TestResults = testResults.ToArray();
      CoverageReport = coverageReport;
    }
  }
}
