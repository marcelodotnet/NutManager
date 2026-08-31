using System.Text.Json;
using System.Text.Json.Serialization;

namespace NutManager.Core.Agent;

/// <summary>
/// Which ways in the agent listens on.
///
/// The two transports are independent, not a mode selector. A server may expose the named pipe, or
/// HTTPS, or both at once — what it may never expose is neither, because an agent with no transport
/// is a service that starts, occupies its service slot and answers nobody. That invariant is a type
/// which cannot hold the invalid combination rather than a rule the UI is trusted to remember.
///
/// Note what this does <em>not</em> mean for a client. A desktop profile that selected HTTPS still
/// talks HTTPS and one that selected the named pipe still talks to the pipe; a server offering both
/// is not permission for either to fall back to the other.
/// </summary>
public sealed record AgentTransportSelection
{
    private AgentTransportSelection(bool namedPipeEnabled, bool httpsEnabled)
    {
        NamedPipeEnabled = namedPipeEnabled;
        HttpsEnabled = httpsEnabled;
    }

    /// <summary>What a machine with no configuration file has always had.</summary>
    public static AgentTransportSelection Default { get; } = new(namedPipeEnabled: true, httpsEnabled: false);

    public bool NamedPipeEnabled { get; }

    public bool HttpsEnabled { get; }

    /// <summary>
    /// Whether the named pipe may be turned off right now — that is, whether something else would
    /// still be listening afterwards. The UI asks this rather than recomputing it, so the checkbox
    /// that is drawn and the state that would be saved cannot disagree.
    /// </summary>
    public bool CanDisableNamedPipe => HttpsEnabled;

    public bool CanDisableHttps => NamedPipeEnabled;

    /// <summary>
    /// Builds a selection, or explains why the combination is not one. The failure is returned rather
    /// than thrown because the caller is a checkbox handler, and a click is not an exceptional event.
    /// </summary>
    public static bool TryCreate(bool namedPipeEnabled, bool httpsEnabled, out AgentTransportSelection? selection, out string? failure)
    {
        if (!namedPipeEnabled && !httpsEnabled)
        {
            selection = null;
            failure = "At least one transport must stay enabled; the agent would otherwise start with nothing listening.";
            return false;
        }

        selection = new AgentTransportSelection(namedPipeEnabled, httpsEnabled);
        failure = null;
        return true;
    }

    /// <summary>
    /// The same construction for callers that have already established the combination is valid —
    /// reading back a document this type produced, and tests.
    /// </summary>
    public static AgentTransportSelection Create(bool namedPipeEnabled, bool httpsEnabled)
    {
        if (!TryCreate(namedPipeEnabled, httpsEnabled, out var selection, out var failure))
        {
            throw new ArgumentException(failure, nameof(namedPipeEnabled));
        }

        return selection!;
    }
}

/// <summary>
/// The on-disk shape of <c>agent.json</c>, as the configuration utility writes it and the agent
/// reads it.
///
/// It holds no secret and has nowhere to put one: no password, no PFX, no private key, no client
/// credential. The certificate is named by thumbprint and lives in <c>LocalMachine\My</c>, where the
/// private key is protected by Windows rather than by a file this process could be talked into
/// reading.
///
/// <para>
/// <b>Backward compatibility is a hard requirement.</b> Files written before the named pipe could be
/// switched off carry no <c>namedPipeEnabled</c> member at all, and those installations were
/// listening on the pipe. So the property is nullable and absence means enabled — a plain
/// <c>bool</c> would default to <c>false</c> and silently take the transport away from every existing
/// deployment on upgrade. The nullability is the compatibility mechanism, not an oversight.
/// </para>
/// </summary>
public sealed record AgentTransportConfigurationDocument
{
    /// <summary>
    /// Absent in every file written before this was configurable, and those agents listened on the
    /// pipe. Absent therefore means enabled — see <see cref="NamedPipeIsEnabled"/>.
    /// </summary>
    [JsonPropertyName("namedPipeEnabled")]
    public bool? NamedPipeEnabled { get; init; }

    [JsonPropertyName("httpsEnabled")]
    public bool HttpsEnabled { get; init; }

    /// <summary>The HTTP.sys prefix, for example <c>https://gandalf.sbra.local:5199/</c>.</summary>
    [JsonPropertyName("httpsPrefix")]
    public string? HttpsPrefix { get; init; }

    /// <summary>Identifies the certificate in <c>LocalMachine\My</c>. Never the certificate itself.</summary>
    [JsonPropertyName("certificateThumbprint")]
    public string? CertificateThumbprint { get; init; }

    /// <summary>The resolved answer, with the legacy default applied.</summary>
    [JsonIgnore]
    public bool NamedPipeIsEnabled => NamedPipeEnabled ?? true;

    /// <summary>What is actually listening, given this document.</summary>
    [JsonIgnore]
    public AgentTransportSelection Transports => AgentTransportSelection.Create(NamedPipeIsEnabled, HttpsEnabled);

    /// <summary>
    /// The serializer options both sides use. Shared so the reader and the writer cannot develop
    /// different opinions about casing, comments or how an absent member is written back.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Whether the document describes something the agent could actually start.
    ///
    /// Both transports off is refused here rather than only at startup, so the utility cannot write a
    /// file that leaves the service unable to run. The agent checks again on the way in, because a
    /// file can also be edited by hand.
    /// </summary>
    public static bool Validate(AgentTransportConfigurationDocument document, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!document.NamedPipeIsEnabled && !document.HttpsEnabled)
        {
            failure = "Both transports are disabled; the agent would have nothing to listen on.";
            return false;
        }

        if (!document.HttpsEnabled)
        {
            failure = null;
            return true;
        }

        return AgentHttpsPrefixRules.Validate(document.HttpsPrefix, document.CertificateThumbprint, out failure);
    }
}

/// <summary>
/// The rules an HTTPS prefix has to satisfy before anything is written or bound.
///
/// Pure, so every rejection reason can be asserted without a certificate store, a listener or an
/// elevated process. The agent and the configuration utility both call this, which is what keeps the
/// screen that accepts a value and the service that refuses it from disagreeing about what is valid.
/// </summary>
public static class AgentHttpsPrefixRules
{
    public const int MinimumPort = 1;
    public const int MaximumPort = 65535;

    /// <summary>
    /// Builds the prefix from the parts an operator actually types. The trailing slash is added here
    /// exactly once, so the endpoint displayed on screen is the string that gets bound.
    /// </summary>
    public static string BuildPrefix(string host, int port) =>
        $"https://{host.Trim()}:{port}/";

    public static bool TryBuildPrefix(string? host, int port, out string? prefix, out string? failure)
    {
        prefix = null;

        if (string.IsNullOrWhiteSpace(host))
        {
            failure = "An explicit host or FQDN is required.";
            return false;
        }

        var trimmed = host.Trim();

        // Refused before anything is built. A wildcard binding on a privileged agent accepts requests
        // aimed at any name that resolves to this machine, which is the ambiguity the HTTP.sys
        // documentation warns about, and it is not something to correct silently.
        if (trimmed.StartsWith('*') || trimmed.StartsWith('+') || trimmed.Contains('*', StringComparison.Ordinal))
        {
            failure = "The host must be an explicit name rather than a wildcard.";
            return false;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains(':', StringComparison.Ordinal) ||
            trimmed.Contains(' ', StringComparison.Ordinal))
        {
            failure = "The host must be a bare name, without a scheme, port or path.";
            return false;
        }

        if (port is < MinimumPort or > MaximumPort)
        {
            failure = $"The port must be between {MinimumPort} and {MaximumPort}.";
            return false;
        }

        var candidate = BuildPrefix(trimmed, port);
        if (!Validate(candidate, thumbprint: null, out failure, requireThumbprint: false))
        {
            return false;
        }

        prefix = candidate;
        failure = null;
        return true;
    }

    public static bool Validate(string? prefix, string? thumbprint, out string? failure) =>
        Validate(prefix, thumbprint, out failure, requireThumbprint: true);

    private static bool Validate(string? prefix, string? thumbprint, out string? failure, bool requireThumbprint)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            failure = "HTTPS is enabled but no prefix is configured.";
            return false;
        }

        var value = prefix.Trim();

        if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            failure = "The agent HTTPS prefix must use https.";
            return false;
        }

        if (!value.EndsWith('/'))
        {
            // HTTP.sys requires the trailing slash, and silently appending one would mean the listener
            // binds to something the administrator did not write down.
            failure = "The agent HTTPS prefix must end with a forward slash.";
            return false;
        }

        // Checked before parsing, because Uri cannot represent these at all and the failure would
        // otherwise be reported as a malformed URI — true, but not the reason that matters.
        var authority = value["https://".Length..];
        if (authority.StartsWith('*') || authority.StartsWith('+'))
        {
            failure = "The agent HTTPS prefix must name an explicit host rather than a wildcard.";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            failure = "The agent HTTPS prefix must be an absolute URI naming a host.";
            return false;
        }

        if (uri.Port is < MinimumPort or > MaximumPort)
        {
            failure = $"The port must be between {MinimumPort} and {MaximumPort}.";
            return false;
        }

        if (!requireThumbprint)
        {
            failure = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            failure = "HTTPS is enabled but no certificate thumbprint is configured.";
            return false;
        }

        if (!IsPlausibleThumbprint(thumbprint))
        {
            failure = "The certificate thumbprint is not a hexadecimal value.";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>Thumbprints are hex. Anything else is a typo or an attempt to smuggle a path.</summary>
    public static bool IsPlausibleThumbprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = NormalizeThumbprint(value);
        return trimmed.Length >= 40 && trimmed.All(Uri.IsHexDigit);
    }

    public static string NormalizeThumbprint(string thumbprint) =>
        thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>The host and port of a stored prefix, for a screen that has to show them back.</summary>
    public static bool TrySplit(string? prefix, out string? host, out int port)
    {
        host = null;
        port = 0;

        if (string.IsNullOrWhiteSpace(prefix)) return false;
        if (!Uri.TryCreate(prefix.Trim(), UriKind.Absolute, out var uri)) return false;
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;

        host = uri.Host;
        port = uri.Port;
        return true;
    }
}
