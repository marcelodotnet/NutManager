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
        string label, AgentDiagnosticState state, string? detail, string statusText, string iconKey = "NutIconShield")
    {
        Label = label;
        State = state;
        Detail = detail;
        StatusText = statusText;
        IconKey = iconKey;
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
        string iconKey = "NutIconShield") =>
        new(label, state, detail, StatusTextFor(strings, state), iconKey);

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

    /// <summary>What the combo box shows when the list is closed.</summary>
    public override string ToString() => DisplayName;
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
