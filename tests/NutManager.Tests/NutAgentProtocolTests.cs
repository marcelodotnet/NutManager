using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NutManager.Core.Administration;
using NutManager.Core.Agent;
using NutManager.Infrastructure.Agent;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// The wire contract: framing, decoding and dispatch.
///
/// All of it runs over a <see cref="MemoryStream"/>. A named pipe would add an operating-system
/// dependency without testing anything the framing does not already decide, and the cases that matter
/// here are the hostile ones — a length that lies, a frame that stops half-way, a version this build
/// has never seen — which are far easier to produce as bytes than as a misbehaving client.
/// </summary>
public sealed class NutAgentProtocolTests
{
    // ---------------------------------------------------------------- framing

    [Fact]
    public async Task AWellFormedFrameSurvivesTheRoundTrip()
    {
        var payload = Encoding.UTF8.GetBytes("""{"protocolVersion":1}""");
        using var stream = new MemoryStream();

        await NutAgentFraming.WriteFrameAsync(stream, payload, NutAgentFraming.MaxRequestBytes, default);
        stream.Position = 0;
        var frame = await NutAgentFraming.ReadFrameAsync(stream, NutAgentFraming.MaxRequestBytes, default);

        Assert.Equal(NutAgentFrameStatus.Success, frame.Status);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public async Task APeerThatClosesOnAFrameBoundaryIsNotAnError()
    {
        using var stream = new MemoryStream();

        var frame = await NutAgentFraming.ReadFrameAsync(stream, NutAgentFraming.MaxRequestBytes, default);

        // A client that finished and hung up is the ordinary end of a connection, and reporting it as
        // a protocol failure would fill the log with normal behaviour.
        Assert.Equal(NutAgentFrameStatus.Closed, frame.Status);
    }

    [Fact]
    public async Task AConnectionThatStopsPartWayThroughAFrameIsTruncatedRatherThanClosed()
    {
        using var stream = new MemoryStream(Frame(100, [1, 2, 3, 4, 5]));

        var frame = await NutAgentFraming.ReadFrameAsync(stream, NutAgentFraming.MaxRequestBytes, default);

        Assert.Equal(NutAgentFrameStatus.Truncated, frame.Status);
        Assert.Empty(frame.Payload);
    }

    [Fact]
    public async Task AFrameHeaderThatIsItselfCutShortIsTruncated()
    {
        using var stream = new MemoryStream([0, 0]);

        var frame = await NutAgentFraming.ReadFrameAsync(stream, NutAgentFraming.MaxRequestBytes, default);

        Assert.Equal(NutAgentFrameStatus.Truncated, frame.Status);
    }

    [Fact]
    public async Task AnEmptyFrameIsRefusedByItsDeclaredLength()
    {
        using var stream = new MemoryStream(Frame(0, []));

        var frame = await NutAgentFraming.ReadFrameAsync(stream, NutAgentFraming.MaxRequestBytes, default);

        Assert.Equal(NutAgentFrameStatus.InvalidLength, frame.Status);
    }

    [Fact]
    public async Task ANegativeLengthIsRefusedBeforeAnythingIsAllocated()
    {
        using var stream = new MemoryStream(Frame(-1, []));

        var frame = await NutAgentFraming.ReadFrameAsync(stream, NutAgentFraming.MaxRequestBytes, default);

        Assert.Equal(NutAgentFrameStatus.InvalidLength, frame.Status);
    }

    [Fact]
    public async Task ALengthLargerThanTheEndpointWillEverAcceptIsRefusedWithoutAllocating()
    {
        // The declared size is the attack: answered from the four bytes of the header, so the
        // process never tries to find two gigabytes for a peer that asked it to.
        using var stream = new MemoryStream(Frame(int.MaxValue, [1, 2, 3]));

        var frame = await NutAgentFraming.ReadFrameAsync(stream, NutAgentFraming.MaxRequestBytes, default);

        Assert.Equal(NutAgentFrameStatus.TooLarge, frame.Status);
    }

    [Fact]
    public async Task AFrameLargerThanTheLimitIsNotWrittenEither()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NutAgentFraming.WriteFrameAsync(stream, new byte[NutAgentFraming.MaxRequestBytes + 1], NutAgentFraming.MaxRequestBytes, default));
    }

    // ---------------------------------------------------------------- decoding

    [Fact]
    public void AValidRequestDecodes()
    {
        var id = Guid.NewGuid();
        var payload = NutAgentWireCodec.Serialize(NutAgentRequest.For(NutAgentOperation.Restart, id));

        Assert.True(NutAgentWireCodec.TryReadRequest(payload, out var request, out var failure));
        Assert.Equal(NutAgentResultCode.Success, failure);
        Assert.Equal(NutAgentOperation.Restart, request!.Operation);
        Assert.Equal(id, request.OperationId);
    }

    [Fact]
    public void AnUnknownOperationFailsToParseRatherThanReachingALookup()
    {
        var payload = Encoding.UTF8.GetBytes("""{"protocolVersion":1,"operation":"DeleteEverything","operationId":"11111111-1111-1111-1111-111111111111"}""");

        Assert.False(NutAgentWireCodec.TryReadRequest(payload, out var request, out var failure));
        Assert.Equal(NutAgentResultCode.MalformedRequest, failure);
        Assert.Null(request);
    }

    [Fact]
    public void AnIncompatibleVersionIsNamedAsSuchRatherThanBeingDeserializedToSeeWhatHappens()
    {
        // The version is read before the body is bound, so a future shape this build has never seen
        // is still reported as a version problem instead of a malformed one.
        var payload = Encoding.UTF8.GetBytes("""{"protocolVersion":99,"operation":"Something","unknownField":{"nested":true}}""");

        Assert.False(NutAgentWireCodec.TryReadRequest(payload, out var request, out var failure));
        Assert.Equal(NutAgentResultCode.IncompatibleProtocol, failure);
        Assert.Null(request);
    }

    [Fact]
    public void MalformedJsonIsAnAnswerAndNotAnException()
    {
        var payload = Encoding.UTF8.GetBytes("{ this is not json");

        Assert.False(NutAgentWireCodec.TryReadRequest(payload, out _, out var failure));
        Assert.Equal(NutAgentResultCode.MalformedRequest, failure);
    }

    [Fact]
    public void APayloadWithoutAVersionIsMalformed()
    {
        var payload = Encoding.UTF8.GetBytes("""{"operation":"GetStatus"}""");

        Assert.False(NutAgentWireCodec.TryReadRequest(payload, out _, out var failure));
        Assert.Equal(NutAgentResultCode.MalformedRequest, failure);
    }

    [Fact]
    public void AJsonValueThatIsNotAnObjectIsMalformed()
    {
        Assert.False(NutAgentWireCodec.TryReadRequest(Encoding.UTF8.GetBytes("[1,2,3]"), out _, out var failure));
        Assert.Equal(NutAgentResultCode.MalformedRequest, failure);
    }

    [Fact]
    public void AMutationWithoutAnOperationIdIsRefused()
    {
        // The id is what makes a retry idempotent. Without one, a resent request would be a second
        // intention rather than the same one arriving twice.
        var payload = Encoding.UTF8.GetBytes("""{"protocolVersion":1,"operation":"Stop","operationId":"00000000-0000-0000-0000-000000000000"}""");

        Assert.False(NutAgentWireCodec.TryReadRequest(payload, out _, out var failure));
        Assert.Equal(NutAgentResultCode.MalformedRequest, failure);
    }

    [Fact]
    public void AReadDoesNotNeedAnOperationId()
    {
        var payload = Encoding.UTF8.GetBytes("""{"protocolVersion":1,"operation":"GetStatus","operationId":"00000000-0000-0000-0000-000000000000"}""");

        Assert.True(NutAgentWireCodec.TryReadRequest(payload, out var request, out _));
        Assert.Equal(NutAgentOperation.GetStatus, request!.Operation);
    }

    [Fact]
    public void ARequestCarriesNoServiceNamePathOrCommand()
    {
        // The confused-deputy defence is structural: there is no field to put a target in, so a
        // client cannot redirect the agent at something it did not validate for itself.
        var properties = typeof(NutAgentRequest).GetProperties().Select(property => property.Name).ToArray();

        Assert.Equal(["ProtocolVersion", "Operation", "OperationId"], properties);
    }

    [Fact]
    public void AResponseFromAnIncompatibleAgentIsRejectedByTheClientSideToo()
    {
        var payload = Encoding.UTF8.GetBytes("""{"protocolVersion":99,"code":"Success"}""");

        Assert.False(NutAgentWireCodec.TryReadResponse(payload, out _, out var failure));
        Assert.Equal(NutAgentResultCode.IncompatibleProtocol, failure);
    }

    [Fact]
    public void AStatusQueryFailureRoundTripsWithoutChangingProtocolVersion()
    {
        var response = new NutAgentResponse(
            NutAgentOptions.ProtocolVersion,
            NutAgentResultCode.Success,
            Status: new NutAgentServiceStatus(
                "GANDALF",
                "Network UPS Tools",
                "Network UPS Tools",
                NutServiceState.Unknown,
                null,
                "nut.exe",
                true,
                DateTimeOffset.UtcNow,
                new NutAgentServiceQueryFailure(
                    NutAgentServiceQueryFailureKind.AccessDenied,
                    WindowsNutAgentServiceController.ErrorAccessDenied,
                    nameof(Win32Exception),
                    "The SCM refused the status query.")));

        var payload = NutAgentWireCodec.Serialize(response);
        var decoded = NutAgentWireCodec.TryReadResponse(payload, out var roundTripped, out var failure);

        Assert.True(decoded, failure.ToString());
        Assert.Equal(NutAgentOptions.ProtocolVersion, roundTripped?.ProtocolVersion);
        Assert.Equal(response.Status, roundTripped?.Status);
    }

    // ---------------------------------------------------------------- dispatch

    [Fact]
    public async Task TheDispatcherAnswersAHandshakeWithTheAgentsOwnCapabilities()
    {
        var dispatcher = await BuildDispatcherAsync();

        var response = await dispatcher.DispatchAsync(
            NutAgentRequest.For(NutAgentOperation.Handshake, Guid.NewGuid()), Operator, default);

        Assert.Equal(NutAgentResultCode.Success, response.Code);
        Assert.NotNull(response.Handshake);
        Assert.Null(response.Status);
        Assert.Null(response.Result);
    }

    [Fact]
    public async Task TheDispatcherAnswersAStatusRequestWithStatusAlone()
    {
        var dispatcher = await BuildDispatcherAsync();

        var response = await dispatcher.DispatchAsync(
            NutAgentRequest.For(NutAgentOperation.GetStatus, Guid.NewGuid()), Operator, default);

        Assert.NotNull(response.Status);
        Assert.Null(response.Result);
    }

    [Fact]
    public async Task TheDispatcherCarriesTheOperationResultAndItsCode()
    {
        var dispatcher = await BuildDispatcherAsync();

        var response = await dispatcher.DispatchAsync(
            NutAgentRequest.For(NutAgentOperation.Start, Guid.NewGuid()), Operator, default);

        Assert.NotNull(response.Result);
        Assert.Equal(response.Result!.Code, response.Code);
    }

    [Fact]
    public async Task AnUnauthorizedCallerIsRefusedThroughTheDispatcherJustTheSame()
    {
        // The dispatcher adds no check of its own; it must not accidentally add a bypass either.
        var dispatcher = await BuildDispatcherAsync();

        var response = await dispatcher.DispatchAsync(
            NutAgentRequest.For(NutAgentOperation.Stop, Guid.NewGuid()),
            NutAgentCallerContext.Denied(@"SBRA\intruder", "NamedPipe"),
            default);

        Assert.Equal(NutAgentResultCode.Unauthorized, response.Code);
    }

    // ---------------------------------------------------------------- transport access control

    [Fact]
    public void ThePipeGrantsTheOperatorsGroupAndNobodyBroader()
    {
        if (!OperatingSystem.IsWindows()) return;

        var operators = new SecurityIdentifier(WellKnownSidType.BuiltinPowerUsersSid, null);
        var (allowed, _) = InspectPipeRules(operators);

        Assert.Contains(operators.Value, allowed);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, allowed);

        // The groups that would make the pipe reachable by anyone who can log on.
        foreach (var forbidden in new[] { WellKnownSidType.WorldSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.BuiltinUsersSid })
        {
            Assert.DoesNotContain(new SecurityIdentifier(forbidden, null).Value, allowed);
        }
    }

    [Fact]
    public void ThePipeDeniesAnonymousWithoutDenyingEveryone()
    {
        if (!OperatingSystem.IsWindows()) return;

        var operators = new SecurityIdentifier(WellKnownSidType.BuiltinPowerUsersSid, null);
        var (_, denied) = InspectPipeRules(operators);

        Assert.Contains(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null).Value, denied);

        // A deny outranks every allow, and every operator is also a member of Everyone: denying it
        // would refuse exactly the people the pipe exists for.
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value, denied);
    }

    [Fact]
    public void TheTransportHoldsNoCredentialAndNeverElevates()
    {
        var source = Repository.Read(Path.Combine("src", "NutManager.Agent", "NutAgentNamedPipeServer.cs"));

        foreach (var forbidden in new[]
        {
            "LogonUser", "CredUIPrompt", "IRemoteCredentialStore", "Password", "Process.Start",
            "AdjustTokenPrivileges", "TypeNameHandling", "dynamic ", "Dictionary<string, object>"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static readonly NutAgentCallerContext Operator = new(@"SBRA\operator", true, "NamedPipe");

    /// <summary>
    /// The rules split by type. Annotated rather than guarded at each call site, because a platform
    /// guard does not follow the call into the LINQ lambdas that read them.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string[] Allowed, string[] Denied) InspectPipeRules(SecurityIdentifier operators)
    {
        var rules = WindowsNutAgentPipe.CreateSecurity(operators)
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        return (
            [.. rules.Where(rule => rule.AccessControlType == AccessControlType.Allow).Select(rule => rule.IdentityReference.Value)],
            [.. rules.Where(rule => rule.AccessControlType == AccessControlType.Deny).Select(rule => rule.IdentityReference.Value)]);
    }

    private static byte[] Frame(int declaredLength, byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, declaredLength);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<NutAgentRequestDispatcher> BuildDispatcherAsync()
    {
        var service = new NutAgentApplicationService(
            new StubResolver(), new StubController(), new StubAudit(), new StubAuthorization());
        await service.InitializeAsync(default);
        return new NutAgentRequestDispatcher(service);
    }

    private sealed class StubResolver : INutServiceTargetResolver
    {
        private static readonly NutServiceTarget Target =
            new("Network UPS Tools", "Network UPS Tools", @"C:\Program Files\NUT\sbin\nut.exe", NutAssociationConfidence.BinaryPath);

        public Task<NutServiceTargetResolution> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NutServiceTargetResolution(NutServiceTargetStatus.Resolved, Target));

        public Task<NutServiceTargetResolution> RevalidateAsync(NutServiceTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new NutServiceTargetResolution(NutServiceTargetStatus.Resolved, Target));
    }

    private sealed class StubController : INutServiceController
    {
        private NutServiceState _state = NutServiceState.Stopped;

        public Task<NutAgentServiceStatus> GetStatusAsync(NutServiceTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new NutAgentServiceStatus(
                "GANDALF", target.ServiceName, target.DisplayName, _state, null, "nut.exe", true, DateTimeOffset.UtcNow));

        public Task<NutServiceControlOutcome> StartAsync(NutServiceTarget target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _state = NutServiceState.Running;
            return Task.FromResult(new NutServiceControlOutcome(NutAgentResultCode.Success, _state));
        }

        public Task<NutServiceControlOutcome> StopAsync(NutServiceTarget target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _state = NutServiceState.Stopped;
            return Task.FromResult(new NutServiceControlOutcome(NutAgentResultCode.Success, _state));
        }
    }

    private sealed class StubAudit : INutAgentAuditSink
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> WriteAsync(NutAgentAuditEntry entry, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class StubAuthorization : INutAgentAuthorization
    {
        public bool IsConfigured => true;

        public string? ConfigurationFailure => null;

        public Task<bool> IsAuthorizedAsync(string identity, CancellationToken cancellationToken) =>
            Task.FromResult(identity == @"SBRA\operator");
    }
}
