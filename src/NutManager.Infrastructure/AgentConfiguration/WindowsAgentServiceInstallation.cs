using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// Registers NutManagerAgent with the service control manager, and can register nothing else.
///
/// Every value CreateService takes is a constant in this file or is derived from the running process:
/// the service name, the display name, the type, the start type, the error control, the account and
/// the command line. Nothing reaches this class from the window, and there is no overload that would
/// let it. A class that accepted a name and a path would be a general-purpose service installer that
/// happens to be reachable from an elevated GUI, which is a different and much larger thing to review.
///
/// The binary it registers is the process that is running. It is not looked up in the registry, not
/// read from configuration and not passed in - which is what makes "install" unable to point the name
/// at somebody else's executable. It is validated before use all the same: a host that cannot say
/// where it lives, or that is not the expected apphost, refuses rather than registering a guess.
///
/// It creates the service stopped. Starting it is a separate operator action, exactly as it is after
/// the product installer runs.
///
/// Removal is the same boundary in reverse, and carries one rule the registration does not need: the
/// service being deleted has to prove it is this product's. A name is not proof - anybody can register
/// a service called NutManagerAgent - so the image path is read back and matched against the executable
/// this class is allowed to register, and a mismatch refuses rather than deleting somebody else's
/// service. It deletes the registration and nothing else: not the operators group, not its members,
/// not the configuration, not a certificate, and not one HTTPS resource.
///
/// This is deliberately not everything the MSI does. The installer also owns the Event Log source, as
/// a registry key it may later remove, and the agent refuses to run without it rather than creating
/// its own - that fail-closed boundary is the reason this class does not create it either. Registering
/// the service is the part an application may honestly own.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentServiceInstallation
{
    /// <summary>The one service, and the same name the product installer registers.</summary>
    internal const string ServiceName = WindowsAgentServiceAdministration.ServiceName;

    /// <summary>What services.msc shows, matching the installer's display name.</summary>
    internal const string DisplayName = "NutManager Agent";

    /// <summary>The only argument. The host resolves service mode from it and nothing else.</summary>
    internal const string ServiceArgument = "--service";

    /// <summary>The apphost this is allowed to register, by name.</summary>
    internal const string HostFileName = "NutManager.Agent.exe";

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceDelete = 0x00010000;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceStateStopped = 1;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceInteractiveProcess = 0x00000100;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;

    internal const int ErrorServiceExists = 1073;
    internal const int ErrorServiceMarkedForDelete = 1072;
    internal const int ErrorServiceDoesNotExist = 1060;
    internal const int ErrorServiceNotActive = 1062;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorInvalidData = 13;

    /// <summary>
    /// How long a stop, and then the disappearance of the service, are waited for.
    ///
    /// Short and explicit. Windows removes a service once the last handle to it closes, so an open
    /// services.msc on the machine can hold it indefinitely - and waiting forever for somebody else to
    /// close a console is not something a window should do. When it runs out, the state is reported as
    /// pending rather than as removed.
    /// </summary>
    internal static TimeSpan RemovalTimeout { get; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RemovalPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<string?> _hostPath;
    private readonly Func<string, bool> _fileExists;

    internal WindowsAgentServiceInstallation()
        : this(() => Environment.ProcessPath, File.Exists)
    {
    }

    /// <summary>Test seam for the path rules only. The SCM call itself is never faked.</summary>
    internal WindowsAgentServiceInstallation(Func<string?> hostPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(hostPath);
        ArgumentNullException.ThrowIfNull(fileExists);

        _hostPath = hostPath;
        _fileExists = fileExists;
    }

    /// <summary>
    /// The command line the service would be registered with, or null when the host cannot be trusted.
    ///
    /// Separated from the registration so the rules that decide what may be registered can be
    /// exercised without a service control manager: this is the whole of the "arbitrary executable"
    /// question, and it is answered here.
    /// </summary>
    internal string? ResolveImagePath()
    {
        var path = _hostPath();

        if (string.IsNullOrWhiteSpace(path)) return null;

        // A relative path would resolve against whatever directory the SCM happens to use.
        if (!Path.IsPathFullyQualified(path)) return null;

        if (!string.Equals(Path.GetFileName(path), HostFileName, StringComparison.OrdinalIgnoreCase))
        {
            // Running under a different host - a test runner, a debugger stub, a renamed copy. The
            // name is the only thing that ties the process to the product, so a mismatch refuses.
            return null;
        }

        if (!_fileExists(path)) return null;

        // Quoted, because Program Files has a space in it and an unquoted image path lets Windows
        // try C:\Program.exe first. That is a well-known unquoted-service-path weakness and not one
        // this product is going to introduce from a button.
        return $"\"{path}\" {ServiceArgument}";
    }

    internal AgentServiceInstallation Install()
    {
        if (ResolveImagePath() is not { } imagePath)
        {
            return AgentServiceInstallation.Failed(
                $"The {ServiceName} service was not registered because the running program could not be " +
                $"identified as {HostFileName}.");
        }

        var manager = IntPtr.Zero;
        var service = IntPtr.Zero;

        try
        {
            manager = OpenSCManagerW(null, null, ScManagerCreateService);
            if (manager == IntPtr.Zero)
            {
                return Failure("the Service Control Manager could not be opened", Marshal.GetLastWin32Error());
            }

            service = CreateServiceW(
                manager,
                ServiceName,
                DisplayName,
                ServiceQueryStatus,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                imagePath,
                lpLoadOrderGroup: null,
                lpdwTagId: IntPtr.Zero,
                lpDependencies: null,
                // Null is LocalSystem. Naming any other account would mean a password, and the
                // authorization model deliberately does not have one: the agent decides a caller's
                // rights by group membership rather than by whoever it runs as.
                lpServiceStartName: null,
                lpPassword: null);

            if (service != IntPtr.Zero) return AgentServiceInstallation.Installed;

            var error = Marshal.GetLastWin32Error();

            // Somebody registered it between the button being enabled and being pressed. Reported as
            // what it is, and never followed by a change to the service that is already there.
            if (error == ErrorServiceExists) return AgentServiceInstallation.AlreadyInstalled;

            return Failure("the service could not be registered", error);
        }
        catch (Exception exception)
        {
            return AgentServiceInstallation.Failed(
                $"The {ServiceName} service could not be registered ({exception.GetType().Name}).");
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Stops the service if it is running, deletes it, and waits for the SCM to stop reporting it.
    ///
    /// The wait is the part that matters. DeleteService marks a service for deletion and it goes away
    /// when the last handle closes, so returning success on the API call alone would put "removed" on
    /// screen beside a service the machine still has. This asks until the SCM says it is gone.
    /// </summary>
    internal AgentServiceRemoval Remove(TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);

        var manager = IntPtr.Zero;
        var service = IntPtr.Zero;

        try
        {
            manager = OpenSCManagerW(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
            {
                return RemovalFailure(
                    "the Service Control Manager could not be opened", Marshal.GetLastWin32Error());
            }

            var access = ServiceQueryConfig | ServiceQueryStatus | ServiceStop | ServiceDelete;
            service = OpenServiceW(manager, ServiceName, access);

            if (service == IntPtr.Zero)
            {
                var openError = Marshal.GetLastWin32Error();
                return openError == ErrorServiceDoesNotExist
                    ? AgentServiceRemoval.NotInstalled
                    : RemovalFailure("the service could not be opened for removal", openError);
            }

            // Ownership before anything destructive. The registered configuration has to describe
            // the executable this class is allowed to register; a service that merely borrowed the
            // name belongs to somebody else and is left exactly where it is.
            //
            // Reading the configuration and matching it are two separate questions, and they get two
            // separate answers. A read that failed says so and carries its Win32 code; only a read
            // that succeeded may report that the service belongs to somebody else. Collapsing those
            // told operators that a service this product had registered, and was correctly showing
            // as installed, was not theirs.
            var configuration = ReadConfiguration(service);

            if (!configuration.Read)
            {
                return AgentServiceRemoval.QueryFailed(
                    $"The {ServiceName} service was not removed because its configuration could not be read " +
                    $"(Win32 error {configuration.ErrorCode}: {new Win32Exception(configuration.ErrorCode).Message}).",
                    configuration.ErrorCode);
            }

            if (!IsOwnedService(configuration.ServiceType, configuration.ImagePath!))
            {
                return AgentServiceRemoval.NotOwned(
                    $"A service named {ServiceName} is registered as '{configuration.ImagePath}', which is not " +
                    $"{HostFileName} running as this product's service, so it was left untouched.");
            }

            if (!StopIfRunning(service, time, cancellationToken, out var stopFailure)) return stopFailure!;

            if (!DeleteService(service))
            {
                var deleteError = Marshal.GetLastWin32Error();

                // Already marked by somebody else. The wait below still decides what to report.
                if (deleteError != ErrorServiceMarkedForDelete)
                {
                    return RemovalFailure("the service could not be deleted", deleteError);
                }
            }

            // The handle this function holds is itself one of the references keeping the service
            // registered, so it is released before asking whether the service is gone.
            CloseServiceHandle(service);
            service = IntPtr.Zero;

            return WaitUntilAbsent(manager, time, cancellationToken)
                ? AgentServiceRemoval.Removed
                : AgentServiceRemoval.PendingDeletion;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AgentServiceRemoval.Failed(
                $"The {ServiceName} service could not be removed ({exception.GetType().Name}).");
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Whether a registered service is one this class could have created.
    ///
    /// Four narrow questions, all of which have to answer yes: the service is an own-process Win32
    /// service, its image path parses, the executable it names is <see cref="HostFileName"/>, and the
    /// command line is one this product actually registers.
    ///
    /// What it deliberately does not ask is where that executable lives. The registered service and
    /// the copy running this window are routinely different files - after an upgrade, or when a build
    /// removes the registration another one left, or when the folder it was installed from has since
    /// moved - and requiring the two paths to match would refuse to remove this product's own service
    /// in exactly the situations somebody most needs to. Ownership is about the service being this
    /// product's, not about it being this process.
    ///
    /// Start type and account are not consulted either. An administrator may set the service to
    /// Manual, and that answers nothing about whose service it is.
    /// </summary>
    internal static bool IsOwnedService(uint serviceType, string imagePath)
    {
        // Interactive is a flag on top of the type rather than a type of its own, so it is masked off
        // before comparing. Anything sharing a process is not something this class ever registered.
        if ((serviceType & ~ServiceInteractiveProcess) != ServiceWin32OwnProcess) return false;

        if (!TrySplitCommandLine(imagePath, out var executable, out var arguments)) return false;

        if (!string.Equals(Path.GetFileName(executable), HostFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The current registration passes --service. Releases from before the host was unified
        // registered the same executable with no arguments at all, and those are this product's
        // service too, so removing them has to keep working. Nothing else is accepted: an extra
        // switch, a different switch, or a second path means a command line this class never wrote.
        return arguments.Length == 0
            || string.Equals(arguments, ServiceArgument, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits a registered image path into the executable and the rest of the command line.
    ///
    /// Windows stores this field as it was written, so both forms turn up: quoted, which this class
    /// always writes, and unquoted, which is the more common shape across Windows generally. The
    /// unquoted form is the one that matters here, because the previous parser cut it at the first
    /// space - so an ordinary
    /// <c>C:\Program Files\NutManager Agent\NutManager.Agent.exe --service</c> was read as an
    /// executable named <c>C:\Program</c>, and the product refused to remove its own service.
    ///
    /// Unquoted paths are genuinely ambiguous, and this resolves them the one way that cannot invent
    /// ownership: the executable ends at the first ".exe" followed by a space or by the end of the
    /// string. A path whose real executable hides behind an earlier ".exe " therefore reads as the
    /// earlier one and fails the name check, which is a refusal rather than a guess.
    /// </summary>
    internal static bool TrySplitCommandLine(string imagePath, out string executable, out string arguments)
    {
        executable = string.Empty;
        arguments = string.Empty;

        var value = imagePath?.Trim() ?? string.Empty;
        if (value.Length == 0) return false;

        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            if (closing < 1) return false;

            executable = value[1..closing];
            arguments = value[(closing + 1)..].Trim();
            return executable.Length > 0;
        }

        const string extension = ".exe";

        for (var index = value.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = value.IndexOf(extension, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = index + extension.Length;

            if (end != value.Length && value[end] != ' ') continue;

            executable = value[..end];
            arguments = value[end..].Trim();
            return true;
        }

        // No ".exe" boundary at all. Not something this class registered, and not something to
        // salvage by splitting on whitespace and hoping.
        return false;
    }

    /// <summary>
    /// The registered service type and image path, or the reason Windows would not say.
    ///
    /// The sizing call is Microsoft's documented failure: it returns FALSE, sets
    /// ERROR_INSUFFICIENT_BUFFER, and the byte count it writes is the answer. Any other outcome is a
    /// failure to read rather than a usable size - the same rule the read-only query adapter already
    /// applies, so that both paths agree on what "could not be read" means.
    /// </summary>
    private static ServiceConfigurationRead ReadConfiguration(IntPtr service)
    {
        var sizingSucceeded = QueryServiceConfigW(service, IntPtr.Zero, 0, out var required);
        var sizingError = Marshal.GetLastWin32Error();

        if (sizingSucceeded || sizingError != ErrorInsufficientBuffer || required == 0)
        {
            return ServiceConfigurationRead.Failed(sizingError == 0 ? ErrorInsufficientBuffer : sizingError);
        }

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfigW(service, buffer, required, out _))
            {
                return ServiceConfigurationRead.Failed(Marshal.GetLastWin32Error());
            }

            var configuration = Marshal.PtrToStructure<QueryServiceConfigNative>(buffer);
            var imagePath = Marshal.PtrToStringUni(configuration.BinaryPathName);

            return imagePath is null
                ? ServiceConfigurationRead.Failed(ErrorInvalidData)
                : new ServiceConfigurationRead(true, configuration.ServiceType, imagePath, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>What the SCM said about the registration, or why it said nothing.</summary>
    private readonly record struct ServiceConfigurationRead(
        bool Read, uint ServiceType, string? ImagePath, int ErrorCode)
    {
        internal static ServiceConfigurationRead Failed(int errorCode) => new(false, 0, null, errorCode);
    }

    /// <summary>
    /// Stops the service when it is running, and waits for it to actually stop.
    ///
    /// Reached only after the operator confirmed a removal that says the running service will be
    /// stopped to complete it. Nothing else in this window stops the agent without being asked.
    /// </summary>
    private bool StopIfRunning(
        IntPtr service, TimeProvider time, CancellationToken cancellationToken, out AgentServiceRemoval? failure)
    {
        failure = null;

        if (ReadState(service) is not { } state || state == ServiceStateStopped) return true;

        var status = default(ServiceStatusNative);
        if (!ControlService(service, ServiceControlStop, ref status))
        {
            var error = Marshal.GetLastWin32Error();

            // It stopped between the read and the control request. That is the desired state.
            if (error != ErrorServiceNotActive)
            {
                failure = RemovalFailure("the service could not be stopped", error);
                return false;
            }
        }

        var deadline = time.GetUtcNow() + RemovalTimeout;

        while (time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReadState(service) is not { } current || current == ServiceStateStopped) return true;

            Thread.Sleep(RemovalPollInterval);
        }

        failure = AgentServiceRemoval.Failed(
            $"The {ServiceName} service did not stop within {RemovalTimeout.TotalSeconds:N0} seconds, so it " +
            "was not removed.");
        return false;
    }

    private static uint? ReadState(IntPtr service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcessNative>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)size, out _)
                ? Marshal.PtrToStructure<ServiceStatusProcessNative>(buffer).CurrentState
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Asks the SCM whether the service is gone yet, until it is or the wait runs out.</summary>
    private static bool WaitUntilAbsent(IntPtr manager, TimeProvider time, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + RemovalTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = OpenServiceW(manager, ServiceName, ServiceQueryStatus);

            if (probe == IntPtr.Zero && Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist) return true;
            if (probe != IntPtr.Zero) CloseServiceHandle(probe);

            if (time.GetUtcNow() >= deadline) return false;

            Thread.Sleep(RemovalPollInterval);
        }
    }

    private static AgentServiceRemoval RemovalFailure(string operation, int error) =>
        AgentServiceRemoval.Failed(
            $"The {ServiceName} service {operation} (Win32 error {error}: {new Win32Exception(error).Message}).",
            error);

    private static AgentServiceInstallation Failure(string operation, int error) =>
        AgentServiceInstallation.Failed(
            $"The {ServiceName} service {operation} (Win32 error {error}: {new Win32Exception(error).Message}).",
            error);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CreateServiceW(
        IntPtr manager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(
        IntPtr service, IntPtr configuration, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service, int informationLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatusNative status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>SERVICE_STATUS, as ControlService fills it in.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusNative
{
    internal uint ServiceType;
    internal uint CurrentState;
    internal uint ControlsAccepted;
    internal uint Win32ExitCode;
    internal uint ServiceSpecificExitCode;
    internal uint CheckPoint;
    internal uint WaitHint;
}

/// <summary>
/// SERVICE_STATUS_PROCESS.
///
/// Declared here rather than shared with the query adapter so that the removal path owns its own
/// marshalling: the two files answer different questions and neither should be able to break the
/// other by adjusting a struct for its own use.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusProcessNative
{
    internal uint ServiceType;
    internal uint CurrentState;
    internal uint ControlsAccepted;
    internal uint Win32ExitCode;
    internal uint ServiceSpecificExitCode;
    internal uint CheckPoint;
    internal uint WaitHint;
    internal uint ProcessId;
    internal uint ServiceFlags;
}
