using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// One read-only SCM query for the service state and configuration shown by Agent Config.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentServiceQuery
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryConfig = 0x0001;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorServiceDoesNotExist = 1060;

    private const int ScStatusProcessInfo = 0;

    /// <summary>
    /// Room offered to QUERY_SERVICE_CONFIGW before asking it how much it wants.
    ///
    /// The struct is 64 bytes on x64 and the rest is the strings it points at - a path, a display
    /// name, a load order group, an account and a dependency list. 8 KB covers every service this
    /// product will meet by a wide margin, and the grow-and-retry path is still there for the one
    /// that proves otherwise.
    /// </summary>
    private const int InitialConfigurationBuffer = 8 * 1024;

    private readonly IWindowsAgentServiceNative _native;

    internal WindowsAgentServiceQuery()
        : this(new WindowsAgentServiceNative())
    {
    }

    internal WindowsAgentServiceQuery(IWindowsAgentServiceNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    internal AgentServiceSnapshot Describe(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        try
        {
            var managerValue = _native.OpenServiceControlManager(ScManagerConnect);
            if (managerValue == IntPtr.Zero)
            {
                var error = _native.GetLastError();
                return QueryFailure(
                    AgentServiceState.Unknown,
                    "the Service Control Manager could not be opened",
                    error);
            }

            using var manager = new WindowsAgentServiceQueryHandle(managerValue, _native);

            var access = ServiceQueryConfig | ServiceQueryStatus;
            var serviceValue = _native.OpenService(manager.DangerousGetHandle(), serviceName, access);
            if (serviceValue == IntPtr.Zero)
            {
                var error = _native.GetLastError();
                return error == ErrorServiceDoesNotExist
                    ? AgentServiceSnapshot.NotInstalled()
                    : QueryFailure(AgentServiceState.Unknown, "the service could not be opened for query", error);
            }

            using var service = new WindowsAgentServiceQueryHandle(serviceValue, _native);
            var state = ReadState(service.DangerousGetHandle());
            var configuration = ReadConfiguration(service.DangerousGetHandle());

            var failure = JoinFailures(state.Failure, configuration.Failure);
            var errorCode = configuration.ErrorCode ?? state.ErrorCode;

            return new AgentServiceSnapshot(
                state.State,
                Describe(configuration.StartType),
                failure,
                configuration.StartType,
                configuration.Account,
                errorCode);
        }
        catch (Exception exception)
        {
            return new AgentServiceSnapshot(
                AgentServiceState.Unknown,
                StartMode: null,
                $"The {WindowsAgentServiceAdministration.ServiceName} service could not be queried " +
                $"({exception.GetType().Name}).");
        }
    }

    private ServiceStateResult ReadState(IntPtr service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!_native.QueryServiceStatus(
                    service,
                    ScStatusProcessInfo,
                    buffer,
                    (uint)size,
                    out _))
            {
                var error = _native.GetLastError();
                return new ServiceStateResult(
                    AgentServiceState.Unknown,
                    Win32Failure("the service status could not be read", error),
                    error);
            }

            var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
            return new ServiceStateResult(TranslateState(status.CurrentState), null, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads the start type and the account, offering room before asking for a size.
    ///
    /// The obvious shape is to call once with a null buffer, read ERROR_INSUFFICIENT_BUFFER and the
    /// byte count, then allocate and call again. That is what this did, and it made the whole answer
    /// depend on one call whose only job is to fail in a particular way - so a machine on which that
    /// call failed differently reported no start type and no account, over a service the same query
    /// had just described perfectly well as Stopped.
    ///
    /// A buffer large enough for almost every service is offered first instead. The usual case now
    /// answers on the first call and never exercises the failure shape at all; a configuration that
    /// does not fit still reports ERROR_INSUFFICIENT_BUFFER with the size it wants, and that path is
    /// taken exactly once. Anything else is a real failure and keeps its Win32 code.
    ///
    /// The strings live inside the returned buffer, so they are copied out before it is freed - which
    /// is why the marshalling happens here rather than the pointers being carried outward.
    /// </summary>
    private ServiceConfigurationResult ReadConfiguration(IntPtr service)
    {
        var size = InitialConfigurationBuffer;
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_native.QueryServiceConfig(service, buffer, (uint)size, out var required))
                {
                    var configuration = Marshal.PtrToStructure<QueryServiceConfigNative>(buffer);
                    return new ServiceConfigurationResult(
                        TranslateStartType(configuration.StartType),
                        Marshal.PtrToStringUni(configuration.ServiceStartName),
                        null,
                        null);
                }

                var error = _native.GetLastError();

                // The one recoverable failure: the configuration is bigger than the room offered, and
                // Windows has said how much it needs. Grown once, then tried once more.
                if (error != ErrorInsufficientBuffer || required == 0 || required <= (uint)size)
                {
                    return ServiceConfigurationResult.Failed(
                        Win32Failure("the service configuration could not be read", error),
                        error);
                }

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;

                size = checked((int)required);
                buffer = Marshal.AllocHGlobal(size);
            }

            // Two attempts, and the second was made with the size Windows itself asked for.
            return ServiceConfigurationResult.Failed(
                Win32Failure("the service configuration could not be read", ErrorInsufficientBuffer),
                ErrorInsufficientBuffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static AgentServiceSnapshot QueryFailure(AgentServiceState state, string operation, int error) =>
        new(
            State: state,
            StartMode: null,
            Failure: Win32Failure(operation, error),
            StartType: AgentServiceStartType.Unknown,
            Account: null,
            QueryErrorCode: error);

    private static string Win32Failure(string operation, int error) =>
        $"The {WindowsAgentServiceAdministration.ServiceName} service {operation} " +
        $"(Win32 error {error}: {new Win32Exception(error).Message}).";

    private static string? JoinFailures(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first} {second}";
    }

    private static AgentServiceState TranslateState(uint state) => state switch
    {
        1 => AgentServiceState.Stopped,
        2 => AgentServiceState.StartPending,
        3 => AgentServiceState.StopPending,
        4 => AgentServiceState.Running,
        7 => AgentServiceState.Paused,
        _ => AgentServiceState.Unknown,
    };

    internal static AgentServiceStartType TranslateStartType(uint startType) => startType switch
    {
        0 => AgentServiceStartType.Boot,
        1 => AgentServiceStartType.System,
        2 => AgentServiceStartType.Automatic,
        3 => AgentServiceStartType.Manual,
        4 => AgentServiceStartType.Disabled,
        _ => AgentServiceStartType.Unknown,
    };

    private static string? Describe(AgentServiceStartType startType) => startType switch
    {
        AgentServiceStartType.Boot => "Boot",
        AgentServiceStartType.System => "System",
        AgentServiceStartType.Automatic => "Auto",
        AgentServiceStartType.Manual => "Manual",
        AgentServiceStartType.Disabled => "Disabled",
        _ => null,
    };

    private sealed record ServiceStateResult(
        AgentServiceState State,
        string? Failure,
        int? ErrorCode);

    private sealed record ServiceConfigurationResult(
        AgentServiceStartType StartType,
        string? Account,
        string? Failure,
        int? ErrorCode)
    {
        internal static ServiceConfigurationResult Failed(string failure, int errorCode) =>
            new(AgentServiceStartType.Unknown, null, failure, errorCode);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusProcess
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

/// <summary>
/// Uses pointers exactly as QUERY_SERVICE_CONFIGW defines them. The pointed-to strings live inside
/// the buffer until the reader has copied the values it needs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct QueryServiceConfigNative
{
    internal uint ServiceType;
    internal uint StartType;
    internal uint ErrorControl;
    internal IntPtr BinaryPathName;
    internal IntPtr LoadOrderGroup;
    internal uint TagId;
    internal IntPtr Dependencies;
    internal IntPtr ServiceStartName;
    internal IntPtr DisplayName;
}

internal interface IWindowsAgentServiceNative
{
    IntPtr OpenServiceControlManager(uint desiredAccess);
    IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);
    bool QueryServiceStatus(
        IntPtr service,
        int informationLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded);
    bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint bufferSize, out uint bytesNeeded);
    bool CloseServiceHandle(IntPtr handle);
    int GetLastError();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentServiceNative : IWindowsAgentServiceNative
{
    public IntPtr OpenServiceControlManager(uint desiredAccess) =>
        OpenSCManagerW(null, null, desiredAccess);

    public IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess) =>
        OpenServiceW(manager, serviceName, desiredAccess);

    public bool QueryServiceStatus(
        IntPtr service,
        int informationLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded) =>
        QueryServiceStatusEx(service, informationLevel, buffer, bufferSize, out bytesNeeded);

    public bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint bufferSize, out uint bytesNeeded) =>
        QueryServiceConfigW(service, buffer, bufferSize, out bytesNeeded);

    public bool CloseServiceHandle(IntPtr handle) => CloseServiceHandleNative(handle);

    public int GetLastError() => Marshal.GetLastWin32Error();

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenSCManagerW(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int informationLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(
        IntPtr service,
        IntPtr configuration,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("Advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandleNative(IntPtr handle);
}

internal sealed class WindowsAgentServiceQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly IWindowsAgentServiceNative _native;

    internal WindowsAgentServiceQueryHandle(IntPtr handle, IWindowsAgentServiceNative native)
        : base(ownsHandle: true)
    {
        _native = native;
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => _native.CloseServiceHandle(handle);
}
