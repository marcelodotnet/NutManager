using System.Runtime.Versioning;
using System.ServiceProcess;
using NutManager.Agent.Config;

namespace NutManager.Agent;

/// <summary>
/// The single entry point of the NutManager Agent, in either of its two modes.
///
/// One executable, two modes, never both at once. The service host and the configuration window are
/// different processes with different lifetimes and different accounts: one runs unattended as
/// LocalSystem for as long as the machine is up, the other is opened by an administrator, used, and
/// closed. Sharing an executable is a packaging decision - one apphost, one runtime contract, one
/// file to sign - and it is not an invitation to share a process. Avalonia is never initialised on
/// the service path, and no service is ever started on the window path.
/// </summary>
internal static class Program
{
    internal const int ExitSuccess = 0;
    internal const int ExitUnsupportedPlatform = 1;

    /// <summary>
    /// Refused, not defaulted. An unrecognised command line means the caller wanted something this
    /// process does not do, and both of the things it does are privileged.
    /// </summary>
    internal const int ExitInvalidArguments = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The NutManager agent only runs on Windows.");
            return ExitUnsupportedPlatform;
        }

        return Run(args);
    }

    [SupportedOSPlatform("windows")]
    private static int Run(string[] args) =>
        AgentExecutionModeResolver.Resolve(args, StartedByServiceControlManager()) switch
        {
            AgentExecutionMode.Service => RunService(),
            AgentExecutionMode.Config => RunConfiguration(),
            _ => RefuseUnknownArguments(),
        };

    /// <summary>
    /// Whether this process was started by the service control manager rather than by a person.
    ///
    /// A service runs in session 0, on a window station that has no visible desktop, and that is
    /// precisely what this property reports on Windows: the framework asks the process window station
    /// for its flags and answers false when it cannot be seen. An interactive launch - a shortcut, a
    /// double click, a command prompt - always has a visible station and answers true.
    ///
    /// Chosen over inspecting the parent process because it needs no interop and no new dependency,
    /// and over a marker file or an environment variable because those can be wrong while this cannot
    /// be made wrong by anything an operator would plausibly do.
    /// </summary>
    private static bool StartedByServiceControlManager() => !Environment.UserInteractive;

    [SupportedOSPlatform("windows")]
    private static int RunService()
    {
        // Nothing from the configuration module is touched here. Its assemblies sit in the same
        // directory and are never loaded, because nothing on this path refers to them.
        ServiceBase.Run(new NutAgentWindowsService());
        return ExitSuccess;
    }

    [SupportedOSPlatform("windows")]
    private static int RunConfiguration() => AgentConfigHost.Run();

    private static int RefuseUnknownArguments()
    {
        Console.Error.WriteLine(
            $"Usage: NutManager.Agent.exe [{AgentExecutionModeResolver.ServiceSwitch}|{AgentExecutionModeResolver.ConfigSwitch}]");
        return ExitInvalidArguments;
    }
}
