namespace OpenFleet.Simulator;

public static class Program
{
    public static void Main(string[] args) => SimulatorApp.CreateBuilder(args).Build().Run();
}
