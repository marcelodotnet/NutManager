using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NutManager.Core.Agent;

/// <summary>
/// One request, and it can express nothing else.
///
/// Three fields, all fixed. There is no service name, no path, no command and no free-form property
/// bag, so a client cannot ask the agent to act on something the agent did not choose for itself. The
/// operation is an enum, so an unrecognised verb fails to parse rather than reaching a lookup.
/// </summary>
public sealed record NutAgentRequest(int ProtocolVersion, NutAgentOperation Operation, Guid OperationId)
{
    public static NutAgentRequest For(NutAgentOperation operation, Guid operationId) =>
        new(NutAgentOptions.ProtocolVersion, operation, operationId);
}

/// <summary>
/// One response. Exactly one of the payloads is populated, according to the operation, and the rest
/// stay null rather than being invented.
///
/// <see cref="Hardware"/> was added after the first release and is optional on the wire, which is
/// what lets the protocol version stay where it is: an absent property deserializes to null, and a
/// property an older build has never heard of is ignored rather than treated as malformed. The
/// version is reserved for a change that actually breaks a peer.
/// </summary>
public sealed record NutAgentResponse(
    int ProtocolVersion,
    NutAgentResultCode Code,
    NutAgentHandshake? Handshake = null,
    NutAgentServiceStatus? Status = null,
    NutAgentOperationResult? Result = null,
    NutAgentHardwareSnapshot? Hardware = null,
    string? Detail = null)
{
    public static NutAgentResponse Refused(NutAgentResultCode code, string? detail = null) =>
        new(NutAgentOptions.ProtocolVersion, code, Detail: detail);
}

/// <summary>
/// How the default transport is named on the wire.
///
/// These are protocol identifiers rather than Windows APIs, so they live here on the neutral side.
/// A constant kept inside a platform-annotated class reports every neutral caller that reads it as
/// platform-specific, which is a warning about nothing.
/// </summary>
public static class NutAgentNamedPipe
{
    /// <summary>Versioned in the name, so a future incompatible protocol is a different pipe.</summary>
    public const string PipeName = "NutManager.Agent.v1";

    public const string TransportName = "NamedPipe";
}

/// <summary>Why a frame could not be read. A short read is never confused with a closed connection.</summary>
public enum NutAgentFrameStatus
{
    Success,

    /// <summary>The peer closed cleanly, on a frame boundary. Not an error.</summary>
    Closed,

    /// <summary>The peer vanished part-way through a frame it had already announced.</summary>
    Truncated,

    /// <summary>The declared length was zero or negative.</summary>
    InvalidLength,

    /// <summary>The declared length exceeded what this endpoint will ever allocate.</summary>
    TooLarge
}

public sealed record NutAgentFrame(NutAgentFrameStatus Status, byte[] Payload)
{
    public static readonly NutAgentFrame Closed = new(NutAgentFrameStatus.Closed, []);
}

/// <summary>
/// Length-prefixed framing over a stream.
///
/// The length is read before anything is allocated, and it is checked before it is trusted: a peer
/// that announces two gigabytes gets an error, not an allocation. Reads are exact, because a stream
/// is free to return fewer bytes than asked for and treating a short read as a complete message is
/// how a parser starts interpreting the middle of one message as the start of the next.
/// </summary>
public static class NutAgentFraming
{
    /// <summary>A request carries three scalars. Anything larger is not a request this agent defined.</summary>
    public const int MaxRequestBytes = 8 * 1024;

    /// <summary>A response carries one small record. The ceiling is generous and still bounded.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    private const int LengthPrefixBytes = 4;

    public static async Task WriteFrameAsync(Stream stream, byte[] payload, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Length == 0 || payload.Length > maximumBytes)
        {
            throw new InvalidOperationException($"A frame of {payload.Length} bytes is outside the permitted range.");
        }

        var prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<NutAgentFrame> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[LengthPrefixBytes];
        var prefixRead = await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixRead == 0) return NutAgentFrame.Closed;
        if (prefixRead < LengthPrefixBytes) return new NutAgentFrame(NutAgentFrameStatus.Truncated, []);

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0) return new NutAgentFrame(NutAgentFrameStatus.InvalidLength, []);
        if (length > maximumBytes) return new NutAgentFrame(NutAgentFrameStatus.TooLarge, []);

        var payload = new byte[length];
        var payloadRead = await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payloadRead < length
            ? new NutAgentFrame(NutAgentFrameStatus.Truncated, [])
            : new NutAgentFrame(NutAgentFrameStatus.Success, payload);
    }

    /// <summary>Fills the buffer or reports how far it got. Zero means the peer closed before the frame began.</summary>
    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return total;
    }
}

/// <summary>
/// Turns frames into requests and responses.
///
/// The version is read before the body is bound. A future client whose request has a shape this
/// build has never seen must be told the protocol is incompatible, and that answer is only possible
/// if the version is established without first deserializing into a contract that would reject it as
/// malformed for the wrong reason.
/// </summary>
public static class NutAgentWireCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Deliberately absent: no polymorphic type resolution, no converters that could bind a
        // payload to a type the sender named. The DTOs are the only shapes this codec knows.
        Converters = { new JsonStringEnumConverter() }
    };

    private const string ProtocolVersionProperty = "protocolVersion";

    public static byte[] Serialize(NutAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.SerializeToUtf8Bytes(request, Options);
    }

    public static byte[] Serialize(NutAgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return JsonSerializer.SerializeToUtf8Bytes(response, Options);
    }

    /// <summary>
    /// Reads a request, or says precisely why it will not. Never throws for bad input: a malformed
    /// payload is an answer the agent sends back, not an exception that kills a connection handler.
    /// </summary>
    public static bool TryReadRequest(ReadOnlySpan<byte> payload, out NutAgentRequest? request, out NutAgentResultCode failure)
    {
        request = null;

        int declaredVersion;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(ProtocolVersionProperty, out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out declaredVersion))
            {
                failure = NutAgentResultCode.MalformedRequest;
                return false;
            }
        }
        catch (JsonException)
        {
            failure = NutAgentResultCode.MalformedRequest;
            return false;
        }

        if (declaredVersion != NutAgentOptions.ProtocolVersion)
        {
            failure = NutAgentResultCode.IncompatibleProtocol;
            return false;
        }

        NutAgentRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<NutAgentRequest>(payload, Options);
        }
        catch (JsonException)
        {
            failure = NutAgentResultCode.MalformedRequest;
            return false;
        }

        if (parsed is null || !Enum.IsDefined(parsed.Operation))
        {
            failure = NutAgentResultCode.MalformedRequest;
            return false;
        }

        // A mutation is deduplicated by its id, so an absent id would make every retry a second
        // intention. Reads do not need one.
        if (parsed.Operation is NutAgentOperation.Start or NutAgentOperation.Stop or NutAgentOperation.Restart &&
            parsed.OperationId == Guid.Empty)
        {
            failure = NutAgentResultCode.MalformedRequest;
            return false;
        }

        request = parsed;
        failure = NutAgentResultCode.Success;
        return true;
    }

    public static bool TryReadResponse(ReadOnlySpan<byte> payload, out NutAgentResponse? response, out NutAgentResultCode failure)
    {
        response = null;

        try
        {
            var parsed = JsonSerializer.Deserialize<NutAgentResponse>(payload, Options);
            if (parsed is null)
            {
                failure = NutAgentResultCode.MalformedRequest;
                return false;
            }

            if (parsed.ProtocolVersion != NutAgentOptions.ProtocolVersion)
            {
                failure = NutAgentResultCode.IncompatibleProtocol;
                return false;
            }

            response = parsed;
            failure = NutAgentResultCode.Success;
            return true;
        }
        catch (JsonException)
        {
            failure = NutAgentResultCode.MalformedRequest;
            return false;
        }
    }
}
