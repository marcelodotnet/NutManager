using System.Diagnostics;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// Opens the NutManager project page in the operator's default browser.
///
/// The address is a constant compiled into this file. Nothing reaches it from configuration, from the
/// UI, or from a caller, so there is no path by which this becomes a way to launch something else:
/// the only reachable behaviour is "open this one page".
///
/// UseShellExecute with an https address asks the shell to resolve the default handler for that
/// scheme. It does not start a shell, a command interpreter, or any named executable, and no argument
/// is composed from anything variable.
/// </summary>
public sealed class WindowsAgentProjectPageLauncher : IAgentProjectPageLauncher
{
    private const string ProjectPage = "https://github.com/marcelodotnet/NutManager";

    public string ProjectPageUrl => ProjectPage;

    public bool OpenProjectPage()
    {
        try
        {
            using var opened = Process.Start(new ProcessStartInfo(ProjectPage) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            // No default browser, or the shell refused. The About surface still shows the address, so
            // the operator can copy it; a machine without a browser is not an error worth throwing.
            return false;
        }
    }
}
