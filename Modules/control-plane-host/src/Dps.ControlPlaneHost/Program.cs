namespace Dps.ControlPlaneHost;

public static class Program
{
    public static int Main(string[] args) => HostStartup.Run(args, Console.Out, Console.Error);
}
