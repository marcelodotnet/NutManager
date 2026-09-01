using System.Runtime.InteropServices;
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

    private readonly WindowsAgentServiceQuery _query;
    private readonly WindowsAgentServiceInstallation _installation;

    public WindowsAgentServiceAdministration()
        : this(new WindowsAgentServiceQuery(), new WindowsAgentServiceInstallation())
    {
    }

    internal WindowsAgentServiceAdministration(WindowsAgentServiceQuery query)
        : this(query, new WindowsAgentServiceInstallation())
    {
    }

    internal WindowsAgentServiceAdministration(
        WindowsAgentServiceQuery query, WindowsAgentServiceInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(installation);

        _query = query;
        _installation = installation;
    }

    public AgentServiceSnapshot Describe() => _query.Describe(ServiceName);

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
    /// Changes the start type through the service control manager, and touches nothing else.
    ///
    /// ChangeServiceConfig with SERVICE_NO_CHANGE in every other field, so the binary path, the
    /// account, the dependencies and the display name are carried through untouched - this call can
    /// only ever move the service between starting with Windows and not. It never starts or stops
    /// anything: the running state is a separate concern with its own explicit commands.
    ///
    /// The Win32 API rather than sc.exe or a WMI method call, for the same reason the rest of this
    /// file uses it: no shell, no argument string, and a return value that says what happened.
    /// </summary>
    public Task<AgentServiceOutcome> SetStartupAsync(
        AgentServiceStartupPreference preference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var manager = IntPtr.Zero;
        var service = IntPtr.Zero;

        try
        {
            manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) return Task.FromResult(StartupFailure());

            service = OpenService(manager, ServiceName, ServiceChangeConfig);
            if (service == IntPtr.Zero) return Task.FromResult(StartupFailure());

            var startType = preference is AgentServiceStartupPreference.Automatic
                ? ServiceAutoStart
                : ServiceDemandStart;

            var changed = ChangeServiceConfig(
                service, ServiceNoChange, startType, ServiceNoChange,
                null, null, IntPtr.Zero, null, null, null, null);

            return Task.FromResult(changed
                ? new AgentServiceOutcome(true, Describe().State, null)
                : StartupFailure());
        }
        catch (Exception exception)
        {
            return Task.FromResult(new AgentServiceOutcome(
                false, Describe().State,
                $"The {ServiceName} start type could not be changed ({exception.GetType().Name})."));
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Registers the service, and does not start it.
    ///
    /// Delegated rather than inlined so the whole of the "which binary, under which name, with which
    /// arguments" question lives in one small file whose every answer is a constant. There is nothing
    /// to pass in here because there is nothing a caller is allowed to choose.
    ///
    /// Run off the UI thread for the same reason start and stop are: the service control manager can
    /// take a moment, and a window that stops repainting while it does looks like one that has hung.
    /// </summary>
    public Task<AgentServiceInstallation> InstallAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _installation.Install(), cancellationToken);

    private static AgentServiceOutcome StartupFailure() => new(
        false,
        AgentServiceState.Unknown,
        $"The {ServiceName} start type could not be changed (Win32 error {Marshal.GetLastWin32Error()}).");

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceDemandStart = 0x00000003;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

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
