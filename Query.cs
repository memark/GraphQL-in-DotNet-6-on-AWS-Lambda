using static System.Runtime.InteropServices.RuntimeInformation;

public class Query {
  public string SysInfo => $"{FrameworkDescription} running on {RuntimeIdentifier}";
}
