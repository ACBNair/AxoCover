namespace AxoCover.Models.Testing.Adapters
{
  public class NUnit2Adapter : NUnitAdapter
  {
    public override string ExecutorUri => "executor://NUnitTestExecutor";

    public NUnit2Adapter() 
      : base(2, "NUnit.VisualStudio.TestAdapter.dll")
    {

    }
  }
}
