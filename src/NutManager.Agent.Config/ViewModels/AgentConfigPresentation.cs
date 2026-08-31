using System.Globalization;
using NutManager.Agent.Config.Localization;
using NutManager.Core.Agent;

namespace NutManager.Agent.Config.ViewModels;

/// <summary>
/// One status line: a label, a state, and the specific detail behind it.
///
/// The state is carried as an enum and the glyph is derived from it, so colour is never the only
/// signal. A row reads as "✓ NUT detectado — Network UPS Tools" in monochrome, through a screen
/// reader, and to somebody who cannot tell the green from the amber.
/// </summary>
public sealed class AgentStatusItemViewModel
{
    public AgentStatusItemViewModel(
        string label,
        AgentDiagnosticState state,
        string? detail,
        string statusText,
        string iconKey = "NutIconShield",
        string? technicalDetail = null)
    {
        Label = label;
        State = state;
        Detail = detail;
        StatusText = statusText;
        IconKey = iconKey;
        TechnicalDetail = technicalDetail;
    }

    /// <summary>
    /// Which glyph names the thing this row is about — the binding, the reservation, the rule, the
    /// listener. A resource key rather than a geometry, so the view resolves it from the product's own
    /// catalog and this type stays constructible in a test with no Avalonia application running.
    /// </summary>
    public string IconKey { get; }

    /// <summary>
    /// The trailing glyph, which reports the state rather than repeating the subject. Separate from
    /// <see cref="IconKey"/> because one identifies the resource and the other judges it, and a row
    /// where both were the same drawing would be a row saying nothing twice.
    /// </summary>
    public string StateIconKey => State switch
    {
        AgentDiagnosticState.Ready => "AgentIconStateReady",
        AgentDiagnosticState.Attention => "AgentIconStateAttention",
        AgentDiagnosticState.NotConfigured => "AgentIconStateNotConfigured",
        _ => "AgentIconStateError",
    };

    public string Label { get; }

    public AgentDiagnosticState State { get; }

    public string? Detail { get; }

    /// <summary>The state in words, for the accessible name and for anyone not reading colour.</summary>
    public string StatusText { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>
    /// What the platform actually said, kept off the card and put on the tooltip.
    ///
    /// The adapters describe a resource precisely - "Port 5199 is bound by another application
    /// (AppId {...})" - and precision is what makes them useless in a quarter-width column: the text
    /// ran to five lines, pushed the four columns to wildly different heights, and appeared in
    /// English inside the Portuguese window because infrastructure does not localise. None of that
    /// is a reason to lose the information, so it moves rather than going away.
    /// </summary>
    public string? TechnicalDetail { get; }

    public bool HasTechnicalDetail => !string.IsNullOrWhiteSpace(TechnicalDetail);

    /// <summary>
    /// The hover text: what the row is, what state it is in, and the platform's own words underneath.
    /// Assembled here rather than in the view so the order is the same for every row.
    /// </summary>
    public string TooltipText
    {
        get
        {
            var lines = new List<string> { Label };
            if (HasDetail) lines.Add(Detail!);
            if (HasTechnicalDetail) lines.Add(TechnicalDetail!);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// The glyph. Text rather than an icon so it survives being read aloud, copied into a support
    /// email, or rendered somewhere the icon font did not load.
    /// </summary>
    public string Glyph => State switch
    {
        AgentDiagnosticState.Ready => "✓",
        AgentDiagnosticState.Attention => "!",
        AgentDiagnosticState.NotConfigured => "—",
        _ => "✕",
    };

    /// <summary>
    /// The style class the view attaches, mapped onto the product's existing semantic classes rather
    /// than onto colours invented here.
    /// </summary>
    public string StateClass => State switch
    {
        AgentDiagnosticState.Ready => "healthy",
        AgentDiagnosticState.Attention => "warning",
        AgentDiagnosticState.NotConfigured => "muted",
        _ => "critical",
    };

    public string AccessibleText => HasDetail ? $"{Label}: {StatusText}. {Detail}" : $"{Label}: {StatusText}";

    internal static AgentStatusItemViewModel From(
        AgentConfigStrings strings,
        string label,
        AgentDiagnosticState state,
        string? detail = null,
        string iconKey = "NutIconShield",
        string? technicalDetail = null) =>
        new(label, state, detail, StatusTextFor(strings, state), iconKey, technicalDetail);

    internal static string StatusTextFor(AgentConfigStrings strings, AgentDiagnosticState state) => state switch
    {
        AgentDiagnosticState.Ready => strings["Status.Ready"],
        AgentDiagnosticState.Attention => strings["Status.Attention"],
        AgentDiagnosticState.NotConfigured => strings["Status.NotConfigured"],
        _ => strings["Status.Error"],
    };
}

/// <summary>
/// A certificate as the picker shows it.
///
/// Everything an administrator needs to tell two certificates apart without opening the store: what it
/// is for, who issued it, when it stops working. The thumbprint is the identity the configuration
/// stores, so it is shown rather than hidden.
/// </summary>
public sealed class AgentCertificateOption
{
    public AgentCertificateOption(AgentCertificateSummary certificate, AgentConfigStrings strings)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(strings);

        Certificate = certificate;
        IssuerLine = $"{strings["Https.Certificate.Issuer"]}: {AgentCertificateSummaryFormatting.ShortName(certificate.Issuer)}";
        ValidityLine = $"{strings["Https.Certificate.ValidUntil"]}: {certificate.NotAfter:dd/MM/yyyy}";
    }

    public AgentCertificateSummary Certificate { get; }

    public string Thumbprint => Certificate.Thumbprint;

    public string DisplayName => Certificate.DisplayName;

    public string IssuerLine { get; }

    public string ValidityLine { get; }

    /// <summary>
    /// Issuer and expiry on one line, for the inline summary where a line is expensive.
    ///
    /// Both are short, both qualify the same name, and the card has room for two lines rather than
    /// three now that the certificate actions have a row of their own.
    /// </summary>
    public string SummaryLine => $"{IssuerLine}  ·  {ValidityLine}";

    /// <summary>What the combo box shows when the list is closed.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// One certificate as the selection list shows it.
///
/// A wrapper over the summary the catalog already returns, evaluated against the host currently in
/// the draft. It adds no rule of its own: every judgement here comes from
/// <see cref="AgentCertificateRules"/>, which is the same code the endpoint validation and the Apply
/// gate use. A second opinion about what makes a certificate usable is exactly the thing that ends
/// up disagreeing with the first one.
///
/// It holds no key material and no file path - only the summary, which the catalog builds without
/// ever reading a private key.
/// </summary>
public sealed class AgentCertificateCandidate
{
    private readonly AgentConfigStrings _strings;

    public AgentCertificateCandidate(
        AgentCertificateSummary certificate, string host, DateTimeOffset now, AgentConfigStrings strings)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(strings);

        Certificate = certificate;
        _strings = strings;

        MatchesHost = !string.IsNullOrWhiteSpace(host) && AgentCertificateRules.MatchesHost(certificate, host);
        IsCurrentlyValid = certificate.IsCurrentlyValid(now);
        IsUsable = AgentCertificateRules.Evaluate(certificate, host, now).IsUsable;
    }

    public AgentCertificateSummary Certificate { get; }

    public string Thumbprint => Certificate.Thumbprint;

    public string DisplayName => Certificate.DisplayName;

    public string Subject => Certificate.Subject;

    public string Issuer => Certificate.Issuer;

    public bool HasPrivateKey => Certificate.HasPrivateKey;

    public bool SupportsServerAuthentication => Certificate.SupportsServerAuthentication;

    public bool MatchesHost { get; }

    public bool IsCurrentlyValid { get; }

    /// <summary>Whether this one would be accepted, by the product rules rather than by this class.</summary>
    public bool IsUsable { get; }

    /// <summary>
    /// Enough thumbprint to tell two certificates apart at a glance.
    ///
    /// Not decoration: the machine this was built for holds several certificates with the same common
    /// name and the same issuer, differing only in validity dates and thumbprint. A list showing the
    /// common name alone would ask an operator to choose between identical-looking rows. The full
    /// value is on the row tooltip and in the detail pane.
    /// </summary>
    public string ShortThumbprint => Certificate.Thumbprint.Length <= 16
        ? Certificate.Thumbprint
        : $"{Certificate.Thumbprint[..8]}...{Certificate.Thumbprint[^8..]}";

    public string IssuerLine =>
        $"{_strings["Https.Certificate.Issuer"]}: {AgentCertificateSummaryFormatting.ShortName(Certificate.Issuer)}";

    public string ValidityLine =>
        $"{_strings["Https.Certificate.ValidUntil"]}: {Certificate.NotAfter:dd/MM/yyyy}";

    public string ValidFromText => Certificate.NotBefore.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public string ValidUntilText => Certificate.NotAfter.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public string PrivateKeyText => Present(HasPrivateKey);

    public string ServerAuthenticationText => Present(SupportsServerAuthentication);

    public string HostMatchText => MatchesHost
        ? _strings["Https.Certificate.Match"]
        : _strings["Https.Certificate.Mismatch"];

    /// <summary>
    /// The one line under the name: what is wrong, or that nothing is.
    ///
    /// One reason rather than every reason, in the order that decides what an operator does next. A
    /// list of all the problems would make the rows different heights and bury the difference between
    /// two similar certificates, which is the whole reason this list exists.
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (IsUsable) return _strings["Https.Select.Usable"];

            if (!HasPrivateKey) return _strings["Https.Select.NoPrivateKey"];
            if (!IsCurrentlyValid) return _strings["Https.Select.Expired"];
            if (!SupportsServerAuthentication) return _strings["Https.Select.NoServerAuth"];
            if (!MatchesHost) return _strings["Https.Select.HostMismatch"];

            return _strings["Https.Cert.Unusable"];
        }
    }

    public string AccessibleText => $"{DisplayName}. {SummaryText}. {ShortThumbprint}";

    private string Present(bool value) => value
        ? _strings["Https.Certificate.Yes"]
        : _strings["Https.Certificate.No"];
}

/// <summary>Formatting shared by the picker and the summary line.</summary>
internal static class AgentCertificateSummaryFormatting
{
    /// <summary>
    /// The common name out of a distinguished name, falling back to the whole thing. An issuer line
    /// reading "CN=SBRA-AD-CA, DC=sbra, DC=local" tells an operator less than "SBRA-AD-CA" does.
    /// </summary>
    internal static string ShortName(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName)) return distinguishedName;

        foreach (var part in distinguishedName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                var value = part[3..].Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }

        return distinguishedName;
    }
}

/// <summary>
/// How the Apply banner reads.
///
/// Separate from <c>ApplyDisabledReason</c> on purpose: that one explains why the button cannot be
/// pressed, this one reports what happened when it was. Merging them would mean a screen that
/// cannot tell "you have not finished" apart from "it did not work".
/// </summary>
public enum AgentApplyResultKind
{
    None,
    Success,
    Warning,
    Error,
    Info,
}

/// <summary>
/// Which confirmation the window is waiting on, if any.
///
/// The same shape the desktop application uses for service control: a pending value on the view model,
/// rendered inline, cleared by an explicit answer. No message box, no dialog service, no second window
/// — and it means every confirmation path can be driven from a test.
/// </summary>
public enum AgentConfigConfirmation
{
    None,

    /// <summary>Creating the group would write to the directory rather than to this machine.</summary>
    CreateGroupInDirectory,

    /// <summary>HTTPS is being switched off and system resources exist that could be removed.</summary>
    DisableHttps,

    /// <summary>
    /// The HTTPS configuration is being reset: this product's system resources removed and the
    /// endpoint it saved forgotten. Distinct from <see cref="DisableHttps"/>, which only turns the
    /// transport off and asks what to do with the resources it leaves behind.
    /// </summary>
    ResetHttps,

    /// <summary>Configuration was saved while the service was running.</summary>
    RestartService,
}
