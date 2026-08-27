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
    private static readonly (string Key, MaterialIconKind Kind)[] Map =
    [
        ("NutIconDiagnostics", MaterialIconKind.Pulse),
        ("NutIconNetwork", MaterialIconKind.LanConnect),
        ("NutIconTls", MaterialIconKind.LockCheckOutline),
        ("NutIconCertificate", MaterialIconKind.CertificateOutline),
        ("NutIconShield", MaterialIconKind.ShieldCheckOutline),
        ("NutIconUsers", MaterialIconKind.AccountGroupOutline),
        ("NutIconService", MaterialIconKind.CogSyncOutline),
        ("NutIconRestart", MaterialIconKind.Restart),
        ("NutIconApply", MaterialIconKind.ContentSaveOutline),
        ("NutIconInfo", MaterialIconKind.InformationOutline),
        ("NutIconWarning", MaterialIconKind.AlertOutline),
        ("NutIconSuccess", MaterialIconKind.CheckCircleOutline),
        ("NutIconError", MaterialIconKind.CloseCircleOutline),
        ("NutIconCopy", MaterialIconKind.ContentCopy),
        ("NutIconRefresh", MaterialIconKind.Refresh),
        ("NutIconSmb", MaterialIconKind.FolderNetworkOutline),
    ];

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

    /// <summary>The map, so a test can hold it against the desktop's.</summary>
    public static IReadOnlyList<(string Key, MaterialIconKind Kind)> SuppliedIcons => Map;
}
