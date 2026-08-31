using Avalonia;

namespace NutManager.Agent.Config;

/// <summary>
/// The entry point of the local administration utility.
///
/// It is a window and nothing else: no service, no listener, no scheduled task, and no command line
/// that performs an action. An administrator opens it, changes something deliberately, and closes it.
/// There is no unattended mode on purpose — every operation here alters machine security state, and a
/// switch that performed one with nobody present is a switch that ends up in a script nobody reviewed.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("NutManager Agent Config only runs on Windows.");
            return 1;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Also used by the Avalonia design-time tooling, which requires this exact shape.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
