namespace NutManager.Agent;

/// <summary>Which of the two things this executable was asked to be.</summary>
internal enum AgentExecutionMode
{
    /// <summary>The Windows service host.</summary>
    Service,

    /// <summary>The Avalonia configuration window.</summary>
    Config,

    /// <summary>Nothing recognisable. The process refuses rather than guessing.</summary>
    Invalid,
}

/// <summary>
/// Decides which mode the process runs in, and does nothing else.
///
/// Pure on purpose: the decision is the part with the compatibility burden - an installed service may
/// have been registered years ago with no arguments at all - so it is a function of its inputs and
/// can be exercised for every combination without starting a service or opening a window.
/// </summary>
internal static class AgentExecutionModeResolver
{
    internal const string ServiceSwitch = "--service";
    internal const string ConfigSwitch = "--config";

    /// <summary>
    /// Resolves the mode from the command line and the execution context.
    ///
    /// With no arguments the context decides, and that is deliberate rather than convenient. A
    /// service installed before this executable was unified has an ImagePath with no switch on it,
    /// and it must keep starting as a service after an in-place upgrade; the same bare command typed
    /// by an administrator, or launched by double-clicking the file, must open the window. The two
    /// are told apart by where the process is running, not by hoping the ImagePath was updated.
    ///
    /// Anything else is refused. There is no argument that names a file to run, no argument passed
    /// through to a shell, and no unrecognised switch that quietly falls back to a default - a
    /// process that starts the wrong one of these two modes is a process doing privileged work
    /// nobody asked for.
    /// </summary>
    internal static AgentExecutionMode Resolve(
        IReadOnlyList<string> arguments, bool startedByServiceControlManager)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            return startedByServiceControlManager ? AgentExecutionMode.Service : AgentExecutionMode.Config;
        }

        if (arguments.Count != 1) return AgentExecutionMode.Invalid;

        if (string.Equals(arguments[0], ServiceSwitch, StringComparison.OrdinalIgnoreCase))
        {
            return AgentExecutionMode.Service;
        }

        return string.Equals(arguments[0], ConfigSwitch, StringComparison.OrdinalIgnoreCase)
            ? AgentExecutionMode.Config
            : AgentExecutionMode.Invalid;
    }
}
