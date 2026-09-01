using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NutManager.Core.Agent;
using NutManager.Infrastructure.AgentConfiguration;
using Xunit;

namespace NutManager.Tests;

[SupportedOSPlatform("windows")]
public sealed class T42AgentServiceQueryTests
{
    [Fact]
    public void AutomaticLocalSystemConfigurationComesFromTheScmSnapshot()
    {
        using var native = new FakeWindowsServiceNative(
            currentState: 4,
            startType: 2,
            account: "LocalSystem");

        var snapshot = new WindowsAgentServiceQuery(native).Describe("NutManagerAgent");

        Assert.Equal(AgentServiceState.Running, snapshot.State);
        Assert.Equal(AgentServiceStartType.Automatic, snapshot.StartType);
        Assert.Equal("Auto", snapshot.StartMode);
        Assert.Equal("LocalSystem", snapshot.Account);
        Assert.Null(snapshot.Failure);
        Assert.Null(snapshot.QueryErrorCode);
        Assert.Equal(WindowsAgentServiceQuery.ScManagerConnect, native.ManagerAccess);
        Assert.Equal(
            WindowsAgentServiceQuery.ServiceQueryConfig | WindowsAgentServiceQuery.ServiceQueryStatus,
            native.ServiceAccess);
        Assert.Equal(2, native.ConfigurationQueryCalls);
        Assert.Equal([IntPtr.Zero, native.ConfigurationBuffer], native.ConfigurationBuffers);
        Assert.Equal([0u, 256u], native.ConfigurationBufferSizes);
        Assert.Equal(2, native.ClosedHandles);
    }

    [Fact]
    public void ManualDomainAccountIsCopiedFromQueryServiceConfig()
    {
        using var native = new FakeWindowsServiceNative(
            currentState: 1,
            startType: 3,
            account: @"EXAMPLE\svc_nutmanager");

        var snapshot = new WindowsAgentServiceQuery(native).Describe("NutManagerAgent");

        Assert.Equal(AgentServiceState.Stopped, snapshot.State);
        Assert.Equal(AgentServiceStartType.Manual, snapshot.StartType);
        Assert.Equal(@"EXAMPLE\svc_nutmanager", snapshot.Account);
        Assert.Equal(2, native.ConfigurationQueryCalls);
    }

    [Fact]
    public void MissingServiceIsNotConfusedWithAQueryFailure()
    {
        using var native = new FakeWindowsServiceNative(openServiceError: 1060);

        var snapshot = new WindowsAgentServiceQuery(native).Describe("NutManagerAgent");

        Assert.Equal(AgentServiceState.NotInstalled, snapshot.State);
        Assert.False(snapshot.IsInstalled);
        Assert.Null(snapshot.Failure);
        Assert.Null(snapshot.QueryErrorCode);
        Assert.Equal(0, native.ConfigurationQueryCalls);
    }

    [Fact]
    public void ConfigurationFailurePreservesTheWin32ErrorForDiagnostics()
    {
        using var native = new FakeWindowsServiceNative(secondConfigurationError: 5);

        var snapshot = new WindowsAgentServiceQuery(native).Describe("NutManagerAgent");

        Assert.True(snapshot.IsInstalled);
        Assert.Equal(AgentServiceState.Running, snapshot.State);
        Assert.Equal(AgentServiceStartType.Unknown, snapshot.StartType);
        Assert.Null(snapshot.Account);
        Assert.Equal(5, snapshot.QueryErrorCode);
        Assert.Contains("Win32 error 5", snapshot.Failure, StringComparison.Ordinal);
        Assert.Equal(2, native.ConfigurationQueryCalls);
    }

    [Theory]
    [InlineData(0u, AgentServiceStartType.Boot)]
    [InlineData(1u, AgentServiceStartType.System)]
    [InlineData(2u, AgentServiceStartType.Automatic)]
    [InlineData(3u, AgentServiceStartType.Manual)]
    [InlineData(4u, AgentServiceStartType.Disabled)]
    public void EveryDocumentedScmStartTypeHasAnExplicitMapping(
        uint nativeStartType,
        AgentServiceStartType expected) =>
        Assert.Equal(expected, WindowsAgentServiceQuery.TranslateStartType(nativeStartType));

    private sealed class FakeWindowsServiceNative : IWindowsAgentServiceNative, IDisposable
    {
        private const int ErrorInsufficientBuffer = 122;

        private readonly uint _currentState;
        private readonly uint _startType;
        private readonly int? _openServiceError;
        private readonly int? _secondConfigurationError;
        private readonly IntPtr _account;
        private int _lastError;

        internal FakeWindowsServiceNative(
            uint currentState = 4,
            uint startType = 2,
            string account = "LocalSystem",
            int? openServiceError = null,
            int? secondConfigurationError = null)
        {
            _currentState = currentState;
            _startType = startType;
            _openServiceError = openServiceError;
            _secondConfigurationError = secondConfigurationError;
            _account = Marshal.StringToHGlobalUni(account);
        }

        internal uint ManagerAccess { get; private set; }
        internal uint ServiceAccess { get; private set; }
        internal int ConfigurationQueryCalls { get; private set; }
        internal List<IntPtr> ConfigurationBuffers { get; } = [];
        internal List<uint> ConfigurationBufferSizes { get; } = [];
        internal IntPtr ConfigurationBuffer => ConfigurationBuffers.Count > 1
            ? ConfigurationBuffers[1]
            : IntPtr.Zero;
        internal int ClosedHandles { get; private set; }

        public IntPtr OpenServiceControlManager(uint desiredAccess)
        {
            ManagerAccess = desiredAccess;
            return new IntPtr(1);
        }

        public IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess)
        {
            ServiceAccess = desiredAccess;
            if (_openServiceError is null) return new IntPtr(2);

            _lastError = _openServiceError.Value;
            return IntPtr.Zero;
        }

        public bool QueryServiceStatus(
            IntPtr service,
            int informationLevel,
            IntPtr buffer,
            uint bufferSize,
            out uint bytesNeeded)
        {
            var status = new ServiceStatusProcess { CurrentState = _currentState };
            Marshal.StructureToPtr(status, buffer, fDeleteOld: false);
            bytesNeeded = (uint)Marshal.SizeOf<ServiceStatusProcess>();
            _lastError = 0;
            return true;
        }

        public bool QueryServiceConfig(
            IntPtr service,
            IntPtr buffer,
            uint bufferSize,
            out uint bytesNeeded)
        {
            ConfigurationQueryCalls++;
            ConfigurationBuffers.Add(buffer);
            ConfigurationBufferSizes.Add(bufferSize);
            bytesNeeded = 256;

            if (ConfigurationQueryCalls == 1)
            {
                _lastError = ErrorInsufficientBuffer;
                return false;
            }

            if (_secondConfigurationError is not null)
            {
                _lastError = _secondConfigurationError.Value;
                return false;
            }

            var configuration = new QueryServiceConfigNative
            {
                StartType = _startType,
                ServiceStartName = _account,
            };
            Marshal.StructureToPtr(configuration, buffer, fDeleteOld: false);
            _lastError = 0;
            return true;
        }

        public bool CloseServiceHandle(IntPtr handle)
        {
            ClosedHandles++;
            return true;
        }

        public int GetLastError() => _lastError;

        public void Dispose() => Marshal.FreeHGlobal(_account);
    }
}
