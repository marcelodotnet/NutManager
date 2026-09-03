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
        // One call. Room is offered up front rather than the answer depending on a call whose only
        // job is to fail in a particular way.
        Assert.Equal(1, native.ConfigurationQueryCalls);
        Assert.All(native.ConfigurationBuffers, buffer => Assert.NotEqual(IntPtr.Zero, buffer));
        Assert.All(native.ConfigurationBufferSizes, size => Assert.True(size >= 256));
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
        Assert.Equal(1, native.ConfigurationQueryCalls);
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
        Assert.Equal(1, native.ConfigurationQueryCalls);
    }

    /// <summary>
    /// A configuration too big for the room offered is grown once, using the size Windows asked for.
    ///
    /// The recoverable failure is still handled - it is simply no longer the only way to get an
    /// answer. A service with a long dependency list or a deep path takes this path; every other
    /// service answers on the first call.
    /// </summary>
    [Fact]
    public void AConfigurationLargerThanTheOfferedBufferIsReadOnTheSecondAttempt()
    {
        using var native = new FakeWindowsServiceNative(
            startType: 2, account: "LocalSystem", requiredBytes: 64 * 1024);

        var snapshot = new WindowsAgentServiceQuery(native).Describe("NutManagerAgent");

        Assert.Equal(AgentServiceStartType.Automatic, snapshot.StartType);
        Assert.Equal("LocalSystem", snapshot.Account);
        Assert.Null(snapshot.Failure);

        // Exactly two: the offer, then the size Windows named. Never a third.
        Assert.Equal(2, native.ConfigurationQueryCalls);
        Assert.Equal(64u * 1024u, native.ConfigurationBufferSizes[1]);
    }

    /// <summary>
    /// A service that reports its state but not its configuration keeps the state it reported.
    ///
    /// This is the machine that started all of this: Stopped on screen beside an unknown start type
    /// and an unknown account. The state is real and stays; the configuration is honestly unknown and
    /// carries the Win32 code that explains why.
    /// </summary>
    [Fact]
    public void AReadableStateSurvivesAnUnreadableConfiguration()
    {
        using var native = new FakeWindowsServiceNative(
            currentState: 1, secondConfigurationError: 5);

        var snapshot = new WindowsAgentServiceQuery(native).Describe("NutManagerAgent");

        Assert.Equal(AgentServiceState.Stopped, snapshot.State);
        Assert.True(snapshot.IsInstalled);
        Assert.Equal(AgentServiceStartType.Unknown, snapshot.StartType);
        Assert.Null(snapshot.Account);
        Assert.Equal(5, snapshot.QueryErrorCode);
        Assert.Contains("configuration could not be read", snapshot.Failure, StringComparison.Ordinal);
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
        private readonly uint _requiredBytes;
        private readonly IntPtr _account;
        private int _lastError;

        internal FakeWindowsServiceNative(
            uint currentState = 4,
            uint startType = 2,
            string account = "LocalSystem",
            int? openServiceError = null,
            int? secondConfigurationError = null,
            uint requiredBytes = 256)
        {
            _currentState = currentState;
            _startType = startType;
            _openServiceError = openServiceError;
            _secondConfigurationError = secondConfigurationError;
            _requiredBytes = requiredBytes;
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

        /// <summary>
        /// Answers the way Windows does: by whether the buffer is big enough.
        ///
        /// It used to answer by call number - fail the first, succeed the second - which quietly made
        /// the test a description of one implementation rather than of the API. A caller that offered
        /// enough room on its first call would have been failed by this fake for no reason, which is
        /// exactly the shape the product now uses.
        /// </summary>
        public bool QueryServiceConfig(
            IntPtr service,
            IntPtr buffer,
            uint bufferSize,
            out uint bytesNeeded)
        {
            ConfigurationQueryCalls++;
            ConfigurationBuffers.Add(buffer);
            ConfigurationBufferSizes.Add(bufferSize);
            bytesNeeded = _requiredBytes;

            if (buffer == IntPtr.Zero || bufferSize < _requiredBytes)
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
