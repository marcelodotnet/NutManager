using Avalonia;
using Avalonia.Media;
using Material.Icons;

namespace NutManager.Agent.Config.Presentation;

/// <summary>
/// Fills the icon catalog this window draws from, out of the same library the desktop uses.
///
/// The catalog itself — every <c>NutIcon*</c> key and its fallback drawing — is the desktop's own
/// NutIcons.axaml, linked into this project as a shared resource. What is not shared is the code that
/// swaps those fallbacks for Material geometry: linking that file would compile
/// <c>NutManager.App.Presentation.Themes.NutIconLibrary</c> into a second assembly, and the test
/// project references both, so every mention of the type would become ambiguous.
///
/// So this maps the keys this one window draws, and a test asserts each of them resolves to exactly
/// the kind the desktop's library maps it to. A shield here is that shield, and if somebody changes
/// one of them the test says so rather than the two products quietly diverging.
/// </summary>
public static class AgentConfigIcons
{
    /// <summary>
    /// The catalog keys this window draws. Every kind here is the one the desktop's own library maps
    /// that key to, and a test holds the two side by side — so these cannot drift into being a second,
    /// slightly different icon set.
    /// </summary>
    private static readonly (string Key, MaterialIconKind Kind)[] SharedMap =
    [
        // The desktop maps this key to the cog as well. The catalog drawing behind it is a set of
        // sliders, which is a fine glyph for a settings page and the wrong one for a gear button.
        ("NutIconSettings", MaterialIconKind.CogOutline),
        ("NutIconDiagnostics", MaterialIconKind.Pulse),
        ("NutIconNetwork", MaterialIconKind.Web),
        ("NutIconTls", MaterialIconKind.LockOutline),
        ("NutIconCertificate", MaterialIconKind.CertificateOutline),
        ("NutIconShield", MaterialIconKind.ShieldCheckOutline),
        ("NutIconUsers", MaterialIconKind.AccountGroupOutline),
        ("NutIconService", MaterialIconKind.ServerOutline),
        ("NutIconRestart", MaterialIconKind.Restart),
        ("NutIconApply", MaterialIconKind.CheckCircleOutline),
        ("NutIconInfo", MaterialIconKind.InformationOutline),
        // A disc rather than a hazard triangle, matching NutIconError beside it. The triangle is
        // the road-sign shape for danger, and most of what this key marks is a condition to read
        // rather than a hazard to back away from.
        ("NutIconWarning", MaterialIconKind.AlertCircleOutline),
        ("NutIconSuccess", MaterialIconKind.CheckCircleOutline),
        ("NutIconError", MaterialIconKind.AlertCircleOutline),
        ("NutIconCopy", MaterialIconKind.ContentCopy),
        ("NutIconRefresh", MaterialIconKind.Refresh),
        ("NutIconSmb", MaterialIconKind.FolderNetworkOutline),
        ("NutIconConnection", MaterialIconKind.LanConnect),
        ("NutIconRemote", MaterialIconKind.RemoteDesktop),
        ("NutIconLogs", MaterialIconKind.TextBoxOutline),
        ("NutIconClose", MaterialIconKind.Close),
        ("NutIconSun", MaterialIconKind.WhiteBalanceSunny),
        ("NutIconMoon", MaterialIconKind.WeatherNight),
    ];

    /// <summary>
    /// Glyphs this utility needs and the desktop has no key for. Named with an Agent prefix precisely
    /// so they cannot be mistaken for catalog entries: adding them to NutIcons.axaml would change the
    /// desktop application's own resources, which is not this task's to do.
    /// </summary>
    private static readonly (string Key, MaterialIconKind Kind)[] LocalMap =
    [
        ("AgentIconLock", MaterialIconKind.LockOutline),
        ("AgentIconLockOpen", MaterialIconKind.LockOpenVariantOutline),
        ("AgentIconEye", MaterialIconKind.EyeOutline),

        // Filled state glyphs. The catalog's NutIconSuccess is an outlined check because that is what
        // the desktop draws inline in prose; a status strip reads better with a solid disc, and giving
        // these keys of their own keeps the shared catalog untouched.
        ("AgentIconStateReady", MaterialIconKind.CheckCircle),
        // Attention has no key of its own: every warning in this window - the status strip, the
        // apply banner, a settings result, an unusable certificate and the confirmation overlay -
        // draws NutIconWarning. One glyph for one meaning, and nothing to drift apart.
        ("AgentIconStateError", MaterialIconKind.CloseCircle),
        ("AgentIconStateNotConfigured", MaterialIconKind.MinusCircleOutline),
        // Settings tab glyphs. Agent and About reuse the shared server and information keys above,
        // because the catalog already has exactly the right drawing for both; only these two needed
        // a key of their own.
        // The desktop has no Home key: its first destination is an overview dashboard, and this
        // window has no dashboard to go back to - it goes back to the configuration it opened on.
        ("AgentIconHome", MaterialIconKind.HomeOutline),

        ("AgentIconTabGeneral", MaterialIconKind.TuneVariant),
        // People, for the panel that decides which of them may administer the agent.
        ("AgentIconTabUsers", MaterialIconKind.AccountMultipleOutline),

        // Beside a switch that cannot be used, offering the way to the thing that would make it work.
        ("AgentIconHelp", MaterialIconKind.HelpCircleOutline),

        ("AgentIconImport", MaterialIconKind.FileImportOutline),
        ("AgentIconProhibit", MaterialIconKind.CancelOutline),
    ];

    private static readonly (string Key, MaterialIconKind Kind)[] Map = [.. SharedMap, .. LocalMap];

    /// <summary>
    /// Replaces the fallback drawings this map covers. Called after the dictionaries are composed, so
    /// a kind the installed version of the library has dropped leaves the catalog's own drawing in
    /// place rather than an empty box.
    /// </summary>
    public static void Apply(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var (key, kind) in Map)
        {
            var data = MaterialIconDataProvider.GetData(kind);
            if (string.IsNullOrWhiteSpace(data)) continue;

            application.Resources[key] = Geometry.Parse(data);
        }
    }

    /// <summary>Everything this utility fills in, shared and local alike.</summary>
    public static IReadOnlyList<(string Key, MaterialIconKind Kind)> SuppliedIcons => Map;

    /// <summary>
    /// Only the keys the desktop also owns, so a test can hold these against its library and leave the
    /// Agent-prefixed ones — which the desktop has never heard of — out of the comparison.
    /// </summary>
    public static IReadOnlyList<(string Key, MaterialIconKind Kind)> SharedIcons => SharedMap;
}
