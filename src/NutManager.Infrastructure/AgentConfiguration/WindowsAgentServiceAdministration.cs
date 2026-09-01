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

    public AgentServiceSnapshot Describe()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            // Reading Status is what proves the service exists: ServiceController's constructor does
            // not contact the SCM, so an absent service surfaces here rather than on the line above.
            var state = Translate(controller.Status);
            var configuration = ReadConfiguration();

            return new AgentServiceSnapshot(
                state,
                configuration.StartMode,
                configuration.Failure,
                Translate(configuration.StartMode),
                configuration.Account);
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
    /// <summary>
    /// The start type as an enum, from the words Windows uses.
    ///
    /// "Auto" is what Win32_Service reports for automatic start, with "Automatic" appearing on some
    /// systems, and delayed start still reads as automatic - which is correct for this screen, since
    /// both mean the service comes up with Windows.
    /// </summary>
    private static AgentServiceStartType Translate(string? startMode) => startMode?.Trim() switch
    {
        null or "" => AgentServiceStartType.Unknown,
        var mode when mode.StartsWith("Auto", StringComparison.OrdinalIgnoreCase) => AgentServiceStartType.Automatic,
        var mode when mode.Equals("Manual", StringComparison.OrdinalIgnoreCase) => AgentServiceStartType.Manual,
        var mode when mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase) => AgentServiceStartType.Disabled,
        _ => AgentServiceStartType.Unknown,
    };

    /// <summary>
    /// The two facts the settings page reports about how the service is set up, read together from
    /// the service control manager.
    ///
    /// This was a WMI query against Win32_Service, and on a server where the service was plainly
    /// installed and running it returned nothing at all - which the screen then showed as an unknown
    /// start mode and an unknown account, with no way to tell a real answer from a failed read. WMI
    /// was a second, independent way of asking about a service this file already administers through
    /// the SCM, and it was the one that could fail quietly.
    ///
    /// QueryServiceConfig is the same authority the start type is written through, opened with the
    /// same handles. Read and write can no longer disagree, nothing depends on the WMI service being
    /// healthy, and a failure now carries a Win32 error instead of being indistinguishable from a
    /// service that genuinely has no configured account.
    ///
    /// Read-only: the account is never changed from here, and the start type is changed through
    /// ChangeServiceConfig below rather than through this call.
    /// </summary>
    private static (string? StartMode, string? Account, string? Failure) ReadConfiguration()
    {
        var manager = IntPtr.Zero;
        var service = IntPtr.Zero;

        try
        {
            manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) return ConfigurationFailure();

            service = OpenService(manager, ServiceName, ServiceQueryConfig);
            if (service == IntPtr.Zero) return ConfigurationFailure();

            // Two calls by design: the structure carries pointers into a variable-length buffer whose
            // size depends on the strings this particular service happens to have, so the first call
            // is what asks how much room they need.
            QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
            if (required == 0) return ConfigurationFailure();

            var buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                if (!QueryServiceConfig(service, buffer, required, out _)) return ConfigurationFailure();

                var configuration = Marshal.PtrToStructure<QueryServiceConfigW>(buffer);

                return (Describe(configuration.StartType), configuration.ServiceStartName, null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exception)
        {
            return (null, null,
                $"The {ServiceName} configuration could not be read ({exception.GetType().Name}).");
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    private static (string? StartMode, string? Account, string? Failure) ConfigurationFailure() => (
        null,
        null,
        $"The {ServiceName} configuration could not be read (Win32 error {Marshal.GetLastWin32Error()}).");

    /// <summary>
    /// The SCM start type as the word Windows itself uses for it, so the diagnostics line keeps
    /// reading the way it did when this came from Win32_Service.
    ///
    /// Delayed automatic start is reported by the SCM as SERVICE_AUTO_START with a separate delayed
    /// flag, so it reads as automatic here - which is correct for this screen: both mean the service
    /// comes up with Windows.
    /// </summary>
    private static string? Describe(uint startType) => startType switch
    {
        ServiceBootStart => "Boot",
        ServiceSystemStart => "System",
        ServiceAutoStart => "Auto",
        ServiceDemandStart => "Manual",
        ServiceDisabled => "Disabled",
        _ => null,
    };

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

    private static AgentServiceOutcome StartupFailure() => new(
        false,
        AgentServiceState.Unknown,
        $"The {ServiceName} start type could not be changed (Win32 error {Marshal.GetLastWin32Error()}).");

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceBootStart = 0x00000000;
    private const uint ServiceSystemStart = 0x00000001;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceDemandStart = 0x00000003;

    /// <summary>Reported by Windows, never written by this file. Disabled is not an offered choice.</summary>
    private const uint ServiceDisabled = 0x00000004;

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

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        IntPtr service, IntPtr configuration, uint bufferSize, out uint bytesNeeded);

    /// <summary>
    /// QUERY_SERVICE_CONFIGW. The string fields are pointers into the same buffer rather than inline
    /// characters, which is why the caller allocates by the size the SCM asks for.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QueryServiceConfigW
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        [MarshalAs(UnmanagedType.LPWStr)] public string? BinaryPathName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LoadOrderGroup;
        public uint TagId;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Dependencies;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ServiceStartName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DisplayName;
    }

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
