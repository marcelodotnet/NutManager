using System.Management;
using System.Runtime.Versioning;
using System.ServiceProcess;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// Start, stop and restart, for one service whose name is a constant in this file.
///
/// There is no overload that takes a service name and no field an outside caller can set. That is the
/// security property rather than an omission: a utility able to control a named service is generic SCM
/// administration with a text box in front of it, and this one cannot become that without somebody
/// editing this file and being seen to do it.
///
/// NUT is never named here. The NutManager Agent controls the NUT service under its own authorization
/// rules and its own audit trail; this window administers the agent, and stopping NUT from a
/// configuration screen would route around every one of those rules.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentServiceAdministration : IAgentServiceAdministration
{
    /// <summary>The one service. Deliberately a constant, never a parameter.</summary>
    public const string ServiceName = "NutManagerAgent";

    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(30);

    public AgentServiceSnapshot Describe()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            // Reading Status is what proves the service exists: ServiceController's constructor does
            // not contact the SCM, so an absent service surfaces here rather than on the line above.
            var state = Translate(controller.Status);
            return new AgentServiceSnapshot(state, ReadStartMode(), Failure: null);
        }
        catch (InvalidOperationException)
        {
            // The documented shape of "no such service" from ServiceController.
            return AgentServiceSnapshot.NotInstalled();
        }
        catch (Exception exception)
        {
            return AgentServiceSnapshot.NotInstalled(
                $"The {ServiceName} service could not be queried ({exception.GetType().Name}).");
        }
    }

    public Task<AgentServiceOutcome> StartAsync(CancellationToken cancellationToken) =>
        TransitionAsync(ServiceControllerStatus.Running, start: true, cancellationToken);

    public Task<AgentServiceOutcome> StopAsync(CancellationToken cancellationToken) =>
        TransitionAsync(ServiceControllerStatus.Stopped, start: false, cancellationToken);

    /// <summary>
    /// Stop, then start. Expressed as the two operations rather than as one atomic "restart", because
    /// a restart that fails halfway has left the service stopped — and the caller needs to be told
    /// that, rather than shown a generic failure beside a service it believes is still running.
    /// </summary>
    public async Task<AgentServiceOutcome> RestartAsync(CancellationToken cancellationToken)
    {
        var stopped = await StopAsync(cancellationToken).ConfigureAwait(false);
        if (!stopped.Succeeded) return stopped;

        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<AgentServiceOutcome> TransitionAsync(
        ServiceControllerStatus target, bool start, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                using var controller = new ServiceController(ServiceName);

                if (controller.Status == target)
                {
                    // Already where it was asked to be: the desired state, reached earlier.
                    return new AgentServiceOutcome(true, Translate(controller.Status), null);
                }

                if (start)
                {
                    controller.Start();
                }
                else
                {
                    if (!controller.CanStop)
                    {
                        return new AgentServiceOutcome(
                            false, Translate(controller.Status),
                            $"The {ServiceName} service reports that it cannot be stopped.");
                    }

                    controller.Stop();
                }

                controller.WaitForStatus(target, TransitionTimeout);
                controller.Refresh();

                return new AgentServiceOutcome(true, Translate(controller.Status), null);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                // The agent refuses to start when its preconditions are not met — no operators group,
                // no usable transport — and that refusal arrives here as a service that never reaches
                // Running. Saying so, and pointing at the Event Log, is far more useful than "timed
                // out".
                return new AgentServiceOutcome(
                    false,
                    ReadStateQuietly(),
                    $"The {ServiceName} service did not reach the requested state within {TransitionTimeout.TotalSeconds:N0} seconds. " +
                    "Check the Application event log for the reason it refused to start.");
            }
            catch (InvalidOperationException exception)
            {
                return new AgentServiceOutcome(
                    false, AgentServiceState.NotInstalled,
                    $"The {ServiceName} service could not be controlled ({exception.GetType().Name}).");
            }
            catch (Exception exception)
            {
                return new AgentServiceOutcome(
                    false, ReadStateQuietly(),
                    $"The {ServiceName} service could not be controlled ({exception.GetType().Name}).");
            }
        }, cancellationToken);

    private static AgentServiceState ReadStateQuietly()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return Translate(controller.Status);
        }
        catch (Exception)
        {
            return AgentServiceState.Unknown;
        }
    }

    /// <summary>
    /// The start type, read through WMI because ServiceController does not expose it.
    ///
    /// It matters on this screen for one reason: the installer registers the service as Automatic and
    /// deliberately leaves it stopped, so an operator looking at a stopped service needs to see that
    /// it will come up on the next boot rather than conclude the installation failed.
    /// </summary>
    private static string? ReadStartMode()
    {
        try
        {
            // The name is this file's own constant, so there is no caller-supplied text in the query.
            using var searcher = new ManagementObjectSearcher(
                $"SELECT StartMode FROM Win32_Service WHERE Name = '{ServiceName}'");

            foreach (var item in searcher.Get())
            {
                using var service = (ManagementObject)item;
                return service["StartMode"] as string;
            }

            return null;
        }
        catch (Exception)
        {
            // A start mode that cannot be read is a missing detail, not a failure of the screen.
            return null;
        }
    }

    private static AgentServiceState Translate(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => AgentServiceState.Stopped,
        ServiceControllerStatus.StartPending => AgentServiceState.StartPending,
        ServiceControllerStatus.StopPending => AgentServiceState.StopPending,
        ServiceControllerStatus.Running => AgentServiceState.Running,
        ServiceControllerStatus.Paused => AgentServiceState.Paused,
        // ContinuePending and PausePending are transitions this product never puts the agent into.
        // Unknown is the honest answer, and it is never treated as running.
        _ => AgentServiceState.Unknown,
    };
}
