using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutManager.Core.Agent;

namespace NutManager.Agent;

/// <summary>
/// The agent's server-side configuration, read once at startup.
///
/// It holds no secret and cannot be made to. There is no password, no PFX, no private key and no
/// client credential here — the certificate is named by thumbprint and lives in the Windows
/// certificate store, where the private key is protected by the operating system rather than by a
/// file this process could be tricked into reading.
///
/// A missing file means the named pipe and no HTTPS, which is the default an installation gets by
/// doing nothing. The shape is defined once in
/// <see cref="NutManager.Core.Agent.AgentTransportConfigurationDocument"/>; this type is the agent's
/// reader for it, and the configuration utility is the writer.
/// </summary>
internal sealed record NutAgentHttpsOptions
{
    public const string DirectoryName = "NutManager";
    public const string FileName = "agent.json";

    /// <summary>
    /// Absent in every file written before the pipe could be switched off, and those installations
    /// were listening on it. Absent therefore means enabled — hence the nullable <see cref="bool"/>,
    /// where a plain one would default to <c>false</c> and silently take the transport away from every
    /// existing deployment on upgrade. Resolve it through <see cref="IsNamedPipeEnabled"/> rather than
    /// reading it directly.
    /// </summary>
    public bool? NamedPipeEnabled { get; init; }

    /// <summary>Off unless a deployment deliberately turned it on.</summary>
    public bool HttpsEnabled { get; init; }

    /// <summary>The HTTP.sys prefix, for example <c>https://gandalf.sbra.local:5199/</c>.</summary>
    public string? HttpsPrefix { get; init; }

    /// <summary>Identifies the certificate in LocalMachine\My. Never the certificate itself.</summary>
    public string? CertificateThumbprint { get; init; }

    public static NutAgentHttpsOptions Disabled => new();

    /// <summary>
    /// Where the configuration lives. Under ProgramData because it belongs to the machine rather
    /// than to whoever installed it, and its ACL is a deployment concern documented for the
    /// administrator.
    /// </summary>
    internal static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DirectoryName,
        "Agent",
        FileName);

    /// <summary>
    /// Whether the named pipe is listening, with the legacy default applied. A file that predates the
    /// setting says nothing about the pipe, and those agents were listening on it.
    /// </summary>
    internal static bool IsNamedPipeEnabled(NutAgentHttpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.NamedPipeEnabled ?? true;
    }

    /// <summary>
    /// Reads the file, or returns the default. Unreadable and malformed both mean the default:
    /// a configuration this process cannot understand must not become a listener it did not intend,
    /// and the default is the named pipe alone — the narrowest thing the agent can offer.
    /// </summary>
    internal static NutAgentHttpsOptions Load(string? path = null) => Load(path, out _);

    /// <summary>
    /// The same read, reporting why it fell back. Silence about a file that exists and could not be
    /// parsed is the one outcome an administrator cannot diagnose, so the caller records it.
    /// </summary>
    internal static NutAgentHttpsOptions Load(string? path, out string? loadFailure)
    {
        var target = path ?? DefaultPath;
        loadFailure = null;

        try
        {
            if (!File.Exists(target)) return Disabled;

            var parsed = JsonSerializer.Deserialize<NutAgentHttpsOptions>(
                File.ReadAllText(target),
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    Converters = { new JsonStringEnumConverter() }
                });

            if (parsed is null)
            {
                loadFailure = $"{FileName} contained no configuration; the agent fell back to the named pipe.";
                return Disabled;
            }

            return parsed;
        }
        catch (Exception exception)
        {
            loadFailure = $"{FileName} could not be read ({exception.GetType().Name}); the agent fell back to the named pipe.";
            return Disabled;
        }
    }

    /// <summary>
    /// Whether this configuration is usable as written. Pure, so every rejection reason can be
    /// asserted without a certificate store or a listener.
    ///
    /// A prefix that is not HTTPS is refused rather than corrected: the agent must never end up
    /// listening in plain text because a character was wrong in a file.
    /// </summary>
    internal static bool Validate(NutAgentHttpsOptions options, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.HttpsEnabled)
        {
            failure = null;
            return false;
        }

        // Delegated rather than restated. The configuration utility validates what it is about to
        // write with exactly these rules, so a value the screen accepted cannot be one the service
        // then refuses — the drift that would otherwise be discovered only on a server, at startup.
        return AgentHttpsPrefixRules.Validate(options.HttpsPrefix, options.CertificateThumbprint, out failure);
    }

    /// <summary>Thumbprints are hex. Anything else is a typo or an attempt to smuggle a path.</summary>
    internal static bool IsPlausibleThumbprint(string? value) =>
        AgentHttpsPrefixRules.IsPlausibleThumbprint(value);

    internal static string Normalize(string thumbprint) =>
        AgentHttpsPrefixRules.NormalizeThumbprint(thumbprint);
}

/// <summary>
/// Looks the certificate up in the machine store. Windows-typed, so it lives behind one annotation.
///
/// The agent does not install, generate or trust a certificate. It checks that the one the
/// administrator named exists and has a usable private key, and refuses to start HTTPS otherwise —
/// the alternative is a listener that accepts connections and fails every handshake, which looks
/// like a network problem and is not one.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NutAgentCertificateCheck
{
    internal static bool Exists(string thumbprint, out string? failure)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            var normalized = NutAgentHttpsOptions.Normalize(thumbprint);
            var match = store.Certificates.FirstOrDefault(certificate =>
                string.Equals(certificate.Thumbprint, normalized, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                failure = $"No certificate with thumbprint {normalized} was found in LocalMachine\\My.";
                return false;
            }

            using (match)
            {
                if (!match.HasPrivateKey)
                {
                    failure = "The configured certificate has no private key on this machine.";
                    return false;
                }
            }

            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"The certificate store could not be read ({exception.GetType().Name}).";
            return false;
        }
    }
}
