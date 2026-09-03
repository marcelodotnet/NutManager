namespace NutManager.Core.Agent;

/// <summary>
/// The fixed identities that make a system resource recognisably NutManager's.
///
/// Ownership has to be provable before anything is removed, and a port number does not prove it: an
/// administrator who happened to bind something else to 5199 last year would lose it the first time
/// somebody turned NutManager's HTTPS off. So each resource carries a marker only this product
/// writes, and cleanup matches on the marker.
///
/// These values are permanent. Changing one does not rename an existing resource; it orphans it — the
/// old resource stops being recognised as ours and can then never be cleaned up, because the code
/// that would remove it no longer believes it owns it.
/// </summary>
public static class AgentHttpsResourceIdentity
{
    /// <summary>
    /// The AppId recorded against the HTTP.sys SSL certificate binding. HTTP.sys stores it verbatim
    /// and hands it back on query, which makes it an exact ownership proof rather than a heuristic.
    /// </summary>
    public static Guid HttpServiceAppId { get; } = new("4C2E9A17-6B85-4D30-9F1C-7A0E5D82B463");

    /// <summary>The inbound firewall rule's name. Matched exactly; never by port.</summary>
    public const string FirewallRuleName = "NutManager Agent HTTPS";

    /// <summary>The rule's grouping, so it files with the product in the firewall UI.</summary>
    public const string FirewallRuleGroup = "NutManager";

    public const string FirewallRuleDescription =
        "Inbound HTTPS for the NutManager Agent. Created and removed by NutManager Agent Config.";

    /// <summary>
    /// The account the URL reservation is granted to. The agent runs as LocalSystem, and the
    /// reservation says exactly that and nothing wider.
    /// </summary>
    public const string UrlReservationSecurityDescriptor = "D:(A;;GX;;;SY)";
}

/// <summary>
/// Whether a system resource is ours to remove.
///
/// <see cref="Unknown"/> is not a synonym for absent. It means the question could not be answered —
/// the query failed, or the resource exists carrying a marker that neither matches nor clearly belongs
/// to somebody else. It is treated exactly like <see cref="ForeignOwner"/> when deciding whether to
/// delete, because "I could not tell" and "it is not mine" have the same correct outcome.
/// </summary>
public enum AgentResourceOwnership
{
    Absent,
    OwnedByNutManager,
    ForeignOwner,
    Unknown,
}

/// <summary>One system resource, as found on the machine.</summary>
public sealed record AgentResourceState(AgentResourceOwnership Ownership, string? Detail = null)
{
    /// <summary>
    /// Whether Apply may create or update this resource. An absent resource is safe to create and a
    /// provably NutManager-owned resource is safe to update. Foreign and unknown ownership both stop
    /// the operation before any write; inability to prove ownership is never permission to replace.
    /// </summary>
    public bool MayConfigure =>
        Ownership is AgentResourceOwnership.Absent or AgentResourceOwnership.OwnedByNutManager;

    /// <summary>
    /// The only condition under which this utility deletes anything. Absent needs no removal, and
    /// anything not provably ours is left alone with a warning.
    /// </summary>
    public bool MayRemove => Ownership is AgentResourceOwnership.OwnedByNutManager;

    public static AgentResourceState Absent { get; } = new(AgentResourceOwnership.Absent);
}

/// <summary>The three system resources the HTTPS transport needs, and who owns each right now.</summary>
public sealed record AgentHttpsResourceSnapshot(
    AgentResourceState SslBinding,
    AgentResourceState UrlReservation,
    AgentResourceState FirewallRule)
{
    public static AgentHttpsResourceSnapshot None { get; } =
        new(AgentResourceState.Absent, AgentResourceState.Absent, AgentResourceState.Absent);

    /// <summary>Whether every piece HTTPS actually needs is present and ours.</summary>
    public bool IsFullyConfigured =>
        SslBinding.Ownership is AgentResourceOwnership.OwnedByNutManager &&
        UrlReservation.Ownership is AgentResourceOwnership.OwnedByNutManager;

    /// <summary>Whether anything here exists but is not provably ours — the case that needs a warning.</summary>
    public bool HasForeignResource =>
        SslBinding.Ownership is AgentResourceOwnership.ForeignOwner or AgentResourceOwnership.Unknown ||
        UrlReservation.Ownership is AgentResourceOwnership.ForeignOwner or AgentResourceOwnership.Unknown ||
        FirewallRule.Ownership is AgentResourceOwnership.ForeignOwner or AgentResourceOwnership.Unknown;
}

/// <summary>What the operator asked for: a host, a port and a certificate already in the machine store.</summary>
public sealed record AgentHttpsBinding(string Host, int Port, string CertificateThumbprint)
{
    public string Prefix => AgentHttpsPrefixRules.BuildPrefix(Host, Port);
}

/// <summary>
/// Which system resources to tear down when HTTPS is switched off.
///
/// The certificate is deliberately not on this list and there is no member for it. It was put in the
/// machine store by an administrator, may well be used by something else, and is the one thing here
/// that cannot be recreated by clicking a button. NutManager does not install certificates and does
/// not delete them.
/// </summary>
public sealed record AgentHttpsCleanupRequest(
    bool RemoveFirewallRule,
    bool RemoveSslBinding,
    bool RemoveUrlReservation)
{
    /// <summary>Turn HTTPS off and leave every system resource exactly where it is.</summary>
    public static AgentHttpsCleanupRequest Nothing { get; } = new(false, false, false);

    /// <summary>Everything this product created — subject to each one proving to be ours.</summary>
    public static AgentHttpsCleanupRequest Everything { get; } = new(true, true, true);

    public bool RemovesAnything => RemoveFirewallRule || RemoveSslBinding || RemoveUrlReservation;
}

/// <summary>
/// The outcome of changing system resources, itemised.
///
/// Itemised because a partial result is the normal failure here: the firewall rule may be written and
/// the SSL binding then refused. The caller needs to know which of the three actually changed, both to
/// roll those back and to tell the operator the truth about what the machine now looks like.
/// </summary>
public sealed record AgentHttpsResourceResult(
    bool Succeeded,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped,
    string? Failure)
{
    public static AgentHttpsResourceResult Success(IReadOnlyList<string> applied, IReadOnlyList<string>? skipped = null) =>
        new(true, applied, skipped ?? [], null);

    public static AgentHttpsResourceResult Failed(string failure, IReadOnlyList<string>? applied = null, IReadOnlyList<string>? skipped = null) =>
        new(false, applied ?? [], skipped ?? [], failure);
}

/// <summary>
/// The HTTP.sys and firewall resources the HTTPS transport depends on.
///
/// Everything behind this is a documented Windows API — HttpSetServiceConfiguration and the firewall
/// COM policy object. Nothing here shells out: no netsh, no PowerShell, no sc, no cmd. That is not
/// stylistic. A utility that builds a command line has a place where a host name becomes an argument,
/// and this one deliberately has nowhere for that to happen.
/// </summary>
public interface IAgentHttpsResourceAdministration
{
    /// <summary>Read-only. What is currently bound for this endpoint, and who owns it.</summary>
    AgentHttpsResourceSnapshot Describe(AgentHttpsBinding binding);

    /// <summary>
    /// Creates or updates the SSL binding, the URL reservation and the firewall rule for this
    /// endpoint. Rolls back whatever it changed if a later step fails, and touches nothing it did not
    /// create.
    /// </summary>
    AgentHttpsResourceResult Apply(AgentHttpsBinding binding);

    /// <summary>
    /// Removes the requested resources, and only the ones that prove to be NutManager's. Anything
    /// foreign or unattributable is skipped and reported rather than deleted.
    /// </summary>
    AgentHttpsResourceResult Remove(AgentHttpsBinding binding, AgentHttpsCleanupRequest request);
}

/// <summary>A certificate in LocalMachine\My, described without its private key ever being read.</summary>
public sealed record AgentCertificateSummary(
    string Thumbprint,
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool HasPrivateKey,
    bool SupportsServerAuthentication,
    IReadOnlyList<string> SubjectAlternativeNames)
{
    /// <summary>A short label for a list: the common name when there is one, else the whole subject.</summary>
    public string DisplayName => ExtractCommonName(Subject) ?? Subject;

    public bool IsCurrentlyValid(DateTimeOffset now) => now >= NotBefore && now <= NotAfter;

    internal static string? ExtractCommonName(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;

        foreach (var part in subject.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                var value = part[3..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}

/// <summary>Why a certificate may not be used, or that it may.</summary>
public sealed record AgentCertificateVerdict(bool IsUsable, IReadOnlyList<string> Problems)
{
    public static AgentCertificateVerdict Usable { get; } = new(true, []);
}

/// <summary>
/// The rules a certificate must satisfy before HTTPS is allowed to depend on it.
///
/// Pure, and evaluated up front, because every one of these becomes at bind time an error that looks
/// like a network problem: the client gets a handshake failure and an administrator goes and looks at
/// the firewall. Saying "this certificate expired last Tuesday" on the screen where it was chosen is
/// the entire point.
///
/// There is no option to skip any of it. Nothing in this product turns off certificate validation.
/// </summary>
public static class AgentCertificateRules
{
    public static AgentCertificateVerdict Evaluate(AgentCertificateSummary certificate, string host, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var problems = new List<string>();

        if (!certificate.HasPrivateKey)
        {
            // Without the private key on this machine the handshake cannot be completed at all. It is
            // the most common mistake — a certificate imported without its key — and the least
            // visible from the outside.
            problems.Add("The certificate has no private key on this machine.");
        }

        if (now < certificate.NotBefore)
        {
            problems.Add($"The certificate is not valid until {certificate.NotBefore:yyyy-MM-dd}.");
        }
        else if (now > certificate.NotAfter)
        {
            problems.Add($"The certificate expired on {certificate.NotAfter:yyyy-MM-dd}.");
        }

        if (!certificate.SupportsServerAuthentication)
        {
            problems.Add("The certificate is not marked for server authentication.");
        }

        if (!string.IsNullOrWhiteSpace(host) && !MatchesHost(certificate, host))
        {
            problems.Add($"The certificate does not name '{host}' in its subject or subject alternative names.");
        }

        return problems.Count == 0 ? AgentCertificateVerdict.Usable : new AgentCertificateVerdict(false, problems);
    }

    /// <summary>
    /// Whether the certificate speaks for this host. Subject alternative names first, because that is
    /// where every modern certificate carries its identity; the common name is the fallback.
    /// </summary>
    public static bool MatchesHost(AgentCertificateSummary certificate, string host)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (string.IsNullOrWhiteSpace(host)) return false;

        var target = host.Trim().TrimEnd('.');

        if (certificate.SubjectAlternativeNames.Count > 0)
        {
            return certificate.SubjectAlternativeNames.Any(name => NameMatches(name, target));
        }

        // Common Name is a compatibility fallback only for older certificates that carry no SAN.
        // Once a SAN extension exists it is authoritative; accepting a matching CN beside a wrong
        // SAN would bind HTTPS for a name the certificate explicitly does not cover.
        var commonName = AgentCertificateSummary.ExtractCommonName(certificate.Subject);
        return commonName is not null && NameMatches(commonName, target);
    }

    /// <summary>
    /// One name against one host, with the single level of wildcard TLS actually allows:
    /// <c>*.example.com</c> matches <c>server.example.com</c>, and matches neither
    /// <c>a.b.example.com</c> nor the bare <c>example.com</c>.
    /// </summary>
    private static bool NameMatches(string candidate, string host)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var name = candidate.Trim().TrimEnd('.');

        if (!name.StartsWith("*.", StringComparison.Ordinal))
        {
            return string.Equals(name, host, StringComparison.OrdinalIgnoreCase);
        }

        var suffix = name[1..];
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

        // Exactly one label may be substituted for the star.
        var prefix = host[..^suffix.Length];
        return prefix.Length > 0 && !prefix.Contains('.', StringComparison.Ordinal);
    }
}

/// <summary>
/// The certificates an administrator may choose between, read from LocalMachine\My.
///
/// Read-only by construction: there is no member that imports, exports, generates, moves or deletes a
/// certificate. This utility configures which existing certificate the agent should present, and that
/// is the whole of its relationship with the certificate store.
/// </summary>
public interface IAgentCertificateCatalog
{
    IReadOnlyList<AgentCertificateSummary> List();

    /// <summary>The one with this thumbprint, or null. Never throws merely because it is missing.</summary>
    AgentCertificateSummary? Find(string thumbprint);
}

/// <summary>The outcome of importing one operator-selected certificate file.</summary>
public enum AgentCertificateImportOutcome
{
    Imported,
    PasswordRequired,
    PasswordIncorrect,
    UnsupportedFile,
    InvalidFile,

    /// <summary>
    /// Windows refused the operation for want of rights, not because anything was wrong with the file.
    ///
    /// Separated from <see cref="Failed"/> because the two send an administrator to different places.
    /// "Invalid certificate file" sends them back to the certificate authority for a file that was
    /// always fine; this sends them to the elevation prompt. Both the machine key set and the
    /// LocalMachine\My store report this the same way, so one outcome covers the pair.
    /// </summary>
    AccessDenied,

    /// <summary>Nothing was attempted: this build has no importer wired up.</summary>
    ImporterUnavailable,

    Failed,
}

/// <summary>
/// The result of adding a certificate to <c>LocalMachine\My</c>.
///
/// The password is deliberately absent from this contract. It is an input used only while opening a
/// PKCS#12 file and is never part of a result, configuration document, log or persisted view state.
/// </summary>
public sealed record AgentCertificateImportResult(
    AgentCertificateImportOutcome Outcome,
    AgentCertificateSummary? Certificate = null,
    string? Failure = null)
{
    public static AgentCertificateImportResult Imported(AgentCertificateSummary certificate) =>
        new(AgentCertificateImportOutcome.Imported, certificate);

    public static AgentCertificateImportResult From(AgentCertificateImportOutcome outcome, string? failure = null) =>
        new(outcome, null, failure);
}

/// <summary>
/// Imports an operator-selected certificate file into the Windows machine certificate store.
///
/// The implementation owns file parsing and store access. Callers supply a password only for the
/// duration of one import attempt; implementations must never persist or log it.
/// </summary>
public interface IAgentCertificateImporter
{
    AgentCertificateImportResult Import(string path, string? password);
}

/// <summary>The result of writing <c>agent.json</c>.</summary>
public sealed record AgentConfigurationWriteResult(bool Succeeded, string? Failure)
{
    public static AgentConfigurationWriteResult Success { get; } = new(true, null);

    public static AgentConfigurationWriteResult Failed(string failure) => new(false, failure);
}

/// <summary>
/// Reading and writing the agent's configuration file.
///
/// The write validates, writes a temporary file, flushes it, replaces atomically and reads the result
/// back before reporting success. A half-written JSON file is a service that will not start, on a
/// server that may be nobody's desk.
/// </summary>
public interface IAgentConfigurationStore
{
    string Path { get; }

    bool Exists { get; }

    /// <summary>The current document, or the legacy default when there is no file.</summary>
    AgentTransportConfigurationDocument Read();

    AgentConfigurationWriteResult Write(AgentTransportConfigurationDocument document);
}

/// <summary>What the machine has, for the diagnostics view. Every member is read-only.</summary>
public sealed record AgentRuntimeInventorySnapshot(
    string? DotNetRuntimeVersion,
    string? AspNetCoreRuntimeVersion,
    bool EventLogSourceRegistered,
    bool NutDetected,
    string? NutDetail);

/// <summary>
/// The machine facts the diagnostics view reports and nothing acts on.
///
/// NUT detection here reuses the product's own resolver rather than looking for a directory whose name
/// resembles "NUT" — the same rule that refuses to associate a service merely borrowing the name.
/// </summary>
public interface IAgentRuntimeInventory
{
    Task<AgentRuntimeInventorySnapshot> DescribeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Whether anything is actually accepting connections on the agent's HTTPS endpoint.
///
/// Deliberately not a resource state. The SSL binding, the URL reservation and the firewall rule are
/// configuration — they are written once and stay written — while this is an observation of the
/// machine at a moment, and it is the only part of the strip that can change while a window sits open.
/// </summary>
public enum AgentListenerReachability
{
    /// <summary>Nothing has been asked yet, or there is nothing to ask about.</summary>
    Unknown,

    /// <summary>The endpoint accepted a connection.</summary>
    Listening,

    /// <summary>The endpoint was asked and did not answer.</summary>
    Unreachable,
}

/// <summary>
/// One listener observation.
///
/// The detail is a technical token — a socket error, a timeout — kept for the tooltip and the
/// diagnostics list. It never carries a stack trace and never reaches the status column itself.
/// </summary>
public sealed record AgentListenerObservation(AgentListenerReachability State, string? Detail = null)
{
    public static AgentListenerObservation Unknown { get; } = new(AgentListenerReachability.Unknown);

    public static AgentListenerObservation Listening { get; } = new(AgentListenerReachability.Listening);

    public static AgentListenerObservation Unreachable(string? detail = null) =>
        new(AgentListenerReachability.Unreachable, detail);
}

/// <summary>
/// Asks the endpoint whether it is there.
///
/// It exists because a running service is not a running listener. HTTP.sys can refuse to open a
/// prefix — a certificate that no longer has its private key, a reservation another account holds —
/// and the service stays comfortably in the Running state while nothing is accepting connections.
/// Composing "listening" out of the configuration rows and the service state produced a green light
/// for exactly that machine, so the answer is now observed rather than inferred.
///
/// The observation is read-only by construction: it opens a connection and closes it. It never sends
/// a request, never authenticates, and never touches the service, the binding, the firewall or the
/// configuration file.
/// </summary>
public interface IAgentHttpsListenerProbe
{
    Task<AgentListenerObservation> ProbeAsync(AgentHttpsBinding binding, CancellationToken cancellationToken);
}
