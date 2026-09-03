using Avalonia;

namespace NutManager.Agent.Config;

/// <summary>
/// Starts the local administration window.
///
/// This was the entry point of a second executable. It is now a library entry point the agent host
/// calls when it resolves to configuration mode, which is the whole of the change: the window, its
/// view models and its rules are untouched, and what disappeared is a second apphost declaring the
/// same runtime contract beside the first one.
///
/// It is still a window and nothing else: no service, no listener, no scheduled task, and no command
/// line that performs an action. An administrator opens it, changes something deliberately, and
/// closes it. There is no unattended mode on purpose - every operation here alters machine security
/// state, and a switch that performed one with nobody present is a switch that ends up in a script
/// nobody reviewed.
/// </summary>
public static class AgentConfigHost
{
    /// <summary>
    /// Runs the window to completion and returns the process exit code.
    ///
    /// Deliberately takes no arguments. The host has already decided that this is configuration mode,
    /// and the only switch that got the process here carries no further meaning - passing a command
    /// line through to the UI framework would be handing it input for no reason.
    /// </summary>
    public static int Run()
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        return 0;
    }

    /// <summary>Also used by the Avalonia design-time tooling, which requires this exact shape.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
