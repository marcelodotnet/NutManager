using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using NutManager.Agent;
using NutManager.Core.Administration;
using NutManager.Core.Agent;
using Xunit;

namespace NutManager.Tests;

[SupportedOSPlatform("windows")]
public sealed class NutAgentNamedPipeIdentityTests
{
    private static readonly NutServiceTarget Target = new(
        "Network UPS Tools",
        "Network UPS Tools",
        @"C:\NUT\bin\nut.exe",
        NutAssociationConfidence.BinaryPath);

    [Fact]
    public async Task RunAsClientEndsBeforeAsyncPrivilegedWorkAndTheProcessIdentityCrossesTheAwait()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"NutManagerTests.identity.{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);

        var accepting = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accepting;
        var buffer = new byte[1];
        var reading = server.ReadAsync(buffer, timeout.Token).AsTask();
        await client.WriteAsync(new byte[] { 1 }, timeout.Token);
        await client.FlushAsync(timeout.Token);
        Assert.Equal(1, await reading);
        Assert.Equal(1, buffer[0]);

        TokenImpersonationLevel callerLevel = TokenImpersonationLevel.None;
        TokenImpersonationLevel agentLevel = TokenImpersonationLevel.Anonymous;

        server.RunAsClient(() =>
        {
            using var caller = WindowsIdentity.GetCurrent();
            callerLevel = caller.ImpersonationLevel;
            agentLevel = WindowsNutAgentProcessIdentityScope.Instance.RunAsync(async () =>
            {
                await Task.Yield();
                using var agent = WindowsIdentity.GetCurrent();
                return agent.ImpersonationLevel;
            }).GetAwaiter().GetResult();
        });

        Assert.NotEqual(TokenImpersonationLevel.None, callerLevel);
        Assert.Equal(TokenImpersonationLevel.None, agentLevel);
        using var after = WindowsIdentity.GetCurrent();
        Assert.Equal(TokenImpersonationLevel.None, after.ImpersonationLevel);
    }

    [Fact]
    public async Task ACallerThatCouldNotBeAuthorizedNeverReachesTheDispatcher()
    {
        if (!OperatingSystem.IsWindows()) return;

        var marker = new AsyncLocal<string?>();
        var runner = new MarkerProcessIdentityScope(marker);
        var controller = new AuthorityCheckingController(marker);
        var server = await BuildServerAsync(marker, runner, controller);

        var response = await server.DispatchAuthorizedAsync(
            NutAgentRequest.For(NutAgentOperation.GetStatus, Guid.NewGuid()),
            NutAgentCallerContext.Denied(@"SBRA\intruder", NutAgentNamedPipe.TransportName),
            default);

        Assert.Equal(NutAgentResultCode.Unauthorized, response.Code);
        Assert.Equal(0, runner.Calls);
        Assert.Empty(controller.ObservedAuthorities);
    }

    [Fact]
    public async Task AnAuthorizedCallerWithoutScmAuthorityUsesTheAgentAuthorityForReadsAndMutations()
    {
        if (!OperatingSystem.IsWindows()) return;

        var marker = new AsyncLocal<string?> { Value = "caller-without-scm-rights" };
        var runner = new MarkerProcessIdentityScope(marker);
        var controller = new AuthorityCheckingController(marker);
        var resolver = new AuthorityCheckingResolver(marker);
        var audit = new RecordingAudit(marker);
        var dispatcher = await BuildDispatcherAsync(controller, resolver, audit);
        var server = new NutAgentNamedPipeServer(
            dispatcher,
            WindowsIdentity.GetCurrent().User!,
            runner,
            $"NutManagerTests.identity.{Guid.NewGuid():N}");
        var caller = new NutAgentCallerContext(@"SBRA\operator", true, NutAgentNamedPipe.TransportName);

        var status = await server.DispatchAuthorizedAsync(
            NutAgentRequest.For(NutAgentOperation.GetStatus, Guid.NewGuid()), caller, default);
        var start = await server.DispatchAuthorizedAsync(
            NutAgentRequest.For(NutAgentOperation.Start, Guid.NewGuid()), caller, default);
        var restart = await server.DispatchAuthorizedAsync(
            NutAgentRequest.For(NutAgentOperation.Restart, Guid.NewGuid()), caller, default);
        var stop = await server.DispatchAuthorizedAsync(
            NutAgentRequest.For(NutAgentOperation.Stop, Guid.NewGuid()), caller, default);

        Assert.Equal(NutServiceState.Stopped, stop.Result?.FinalState);
        Assert.Equal(NutServiceState.Running, status.Status?.ServiceState);
        Assert.All(new[] { start, restart, stop }, response => Assert.True(response.Result?.Succeeded));
        Assert.All(controller.ObservedAuthorities, value => Assert.Equal("agent-process", value));
        Assert.All(resolver.RevalidationAuthorities, value => Assert.Equal("agent-process", value));
        Assert.All(audit.Authorities, value => Assert.Equal("agent-process", value));
        Assert.All(audit.Entries, entry =>
        {
            Assert.Equal(@"SBRA\operator", entry.CallerIdentity);
            Assert.Equal(NutAgentNamedPipe.TransportName, entry.Transport);
        });
        Assert.Equal("caller-without-scm-rights", marker.Value);
        Assert.Equal(4, runner.Calls);
    }

    [Fact]
    public async Task NamedPipeAndHttpsContextsPreserveTheSameHandshakeAndStatusPayloads()
    {
        var marker = new AsyncLocal<string?> { Value = "agent-process" };
        var controller = new AuthorityCheckingController(marker);
        var dispatcher = await BuildDispatcherAsync(
            controller, new AuthorityCheckingResolver(marker), new RecordingAudit(marker));
        var handshake = NutAgentRequest.For(NutAgentOperation.Handshake, Guid.NewGuid());
        var status = NutAgentRequest.For(NutAgentOperation.GetStatus, Guid.NewGuid());

        var pipeHandshake = await dispatcher.DispatchAsync(
            handshake, new NutAgentCallerContext(@"SBRA\operator", true, NutAgentNamedPipe.TransportName), default);
        var httpsHandshake = await dispatcher.DispatchAsync(
            handshake, new NutAgentCallerContext(@"SBRA\operator", true, NutAgentHttpsProtocol.TransportName), default);
        var pipeStatus = await dispatcher.DispatchAsync(
            status, new NutAgentCallerContext(@"SBRA\operator", true, NutAgentNamedPipe.TransportName), default);
        var httpsStatus = await dispatcher.DispatchAsync(
            status, new NutAgentCallerContext(@"SBRA\operator", true, NutAgentHttpsProtocol.TransportName), default);

        Assert.NotNull(pipeHandshake.Handshake);
        Assert.NotNull(httpsHandshake.Handshake);
        Assert.Equal(pipeHandshake.Handshake.ProtocolVersion, httpsHandshake.Handshake.ProtocolVersion);
        Assert.Equal(pipeHandshake.Handshake.AgentVersion, httpsHandshake.Handshake.AgentVersion);
        Assert.Equal(pipeHandshake.Handshake.MachineName, httpsHandshake.Handshake.MachineName);
        Assert.Equal(pipeHandshake.Handshake.ControlAvailable, httpsHandshake.Handshake.ControlAvailable);
        Assert.Equal(pipeHandshake.Handshake.ControlUnavailableReason, httpsHandshake.Handshake.ControlUnavailableReason);
        Assert.Equal(pipeHandshake.Handshake.Capabilities, httpsHandshake.Handshake.Capabilities);
        Assert.Equal(pipeStatus.Status, httpsStatus.Status);
        Assert.Equal(Target.ServiceName, pipeStatus.Status?.ServiceName);
        Assert.Equal(Target.DisplayName, pipeStatus.Status?.DisplayName);
        Assert.Equal(NutServiceState.Running, pipeStatus.Status?.ServiceState);
        Assert.Equal(4242, pipeStatus.Status?.ProcessId);
        Assert.Equal("nut.exe", pipeStatus.Status?.ExecutableName);
        Assert.True(pipeStatus.Status?.TargetValidated);
    }

    private static async Task<NutAgentNamedPipeServer> BuildServerAsync(
        AsyncLocal<string?> marker,
        INutAgentProcessIdentityScope runner,
        AuthorityCheckingController controller)
    {
        var dispatcher = await BuildDispatcherAsync(
            controller, new AuthorityCheckingResolver(marker), new RecordingAudit(marker));
        return new NutAgentNamedPipeServer(
            dispatcher,
            WindowsIdentity.GetCurrent().User!,
            runner,
            $"NutManagerTests.identity.{Guid.NewGuid():N}");
    }

    private static async Task<NutAgentRequestDispatcher> BuildDispatcherAsync(
        INutServiceController controller,
        INutServiceTargetResolver resolver,
        INutAgentAuditSink audit)
    {
        var service = new NutAgentApplicationService(
            resolver,
            controller,
            audit,
            new AllowAuthorization(),
            options: new NutAgentOptions { MachineName = "GANDALF", AgentVersion = "1.0.0-test" });
        await service.InitializeAsync(default);
        return new NutAgentRequestDispatcher(service);
    }

    private sealed class MarkerProcessIdentityScope(AsyncLocal<string?> marker) : INutAgentProcessIdentityScope
    {
        public int Calls { get; private set; }

        public async Task<T> RunAsync<T>(Func<Task<T>> operation)
        {
            Calls++;
            var before = marker.Value;
            marker.Value = "agent-process";
            try
            {
                return await operation();
            }
            finally
            {
                marker.Value = before;
            }
        }
    }

    private sealed class AuthorityCheckingResolver(AsyncLocal<string?> marker) : INutServiceTargetResolver
    {
        public List<string?> RevalidationAuthorities { get; } = [];

        public Task<NutServiceTargetResolution> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NutServiceTargetResolution(NutServiceTargetStatus.Resolved, Target));

        public Task<NutServiceTargetResolution> RevalidateAsync(
            NutServiceTarget target,
            CancellationToken cancellationToken)
        {
            RevalidationAuthorities.Add(marker.Value);
            return Task.FromResult(new NutServiceTargetResolution(NutServiceTargetStatus.Resolved, Target));
        }
    }

    private sealed class AuthorityCheckingController(AsyncLocal<string?> marker) : INutServiceController
    {
        private NutServiceState _state = NutServiceState.Running;

        public List<string?> ObservedAuthorities { get; } = [];

        public Task<NutAgentServiceStatus> GetStatusAsync(
            NutServiceTarget target,
            CancellationToken cancellationToken)
        {
            ObserveAuthority();
            return Task.FromResult(new NutAgentServiceStatus(
                "GANDALF",
                target.ServiceName,
                target.DisplayName,
                _state,
                _state == NutServiceState.Running ? 4242 : null,
                "nut.exe",
                true,
                new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)));
        }

        public Task<NutServiceControlOutcome> StartAsync(
            NutServiceTarget target,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ObserveAuthority();
            _state = NutServiceState.Running;
            return Task.FromResult(new NutServiceControlOutcome(NutAgentResultCode.Success, _state));
        }

        public Task<NutServiceControlOutcome> StopAsync(
            NutServiceTarget target,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ObserveAuthority();
            _state = NutServiceState.Stopped;
            return Task.FromResult(new NutServiceControlOutcome(NutAgentResultCode.Success, _state));
        }

        private void ObserveAuthority()
        {
            ObservedAuthorities.Add(marker.Value);
            if (!string.Equals(marker.Value, "agent-process", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("The fake SCM only accepts the Agent authority.");
            }
        }
    }

    private sealed class RecordingAudit(AsyncLocal<string?> marker) : INutAgentAuditSink
    {
        public List<string?> Authorities { get; } = [];
        public List<NutAgentAuditEntry> Entries { get; } = [];

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
        {
            Authorities.Add(marker.Value);
            return Task.FromResult(true);
        }

        public Task<bool> WriteAsync(NutAgentAuditEntry entry, CancellationToken cancellationToken)
        {
            Authorities.Add(marker.Value);
            Entries.Add(entry);
            return Task.FromResult(true);
        }
    }

    private sealed class AllowAuthorization : INutAgentAuthorization
    {
        public bool IsConfigured => true;
        public string? ConfigurationFailure => null;

        public Task<bool> IsAuthorizedAsync(string identity, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
