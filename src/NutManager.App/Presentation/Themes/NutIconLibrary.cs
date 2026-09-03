using Avalonia;
using Avalonia.Media;
using Material.Icons;

namespace NutManager.App.Presentation.Themes;

/// <summary>
/// Fills the semantic icon catalog from Material Icons.
///
/// This is the only place in the application that knows an icon library exists. Views keep asking
/// for <c>{DynamicResource NutIconServer}</c>, so the drawing behind a name can be swapped again —
/// or taken back in-house — without editing a single surface. The library supplies the geometry; no
/// path data is transcribed by hand.
///
/// The map covers every semantic name any view references, so the whole application is drawn from
/// the library at runtime. Five icons used to be composed from several shapes each — LEDs that
/// blinked out of phase, a gear turning around a stationary hub, a dot sweeping along a trace — and
/// those parts are gone: the library gives one shape per name, and the motion those parts carried
/// now moves the whole glyph instead. A single silhouette is worth more than segmented motion,
/// because it is what keeps every icon in the product on one drawing system.
/// </summary>
public static class NutIconLibrary
{
    private static readonly (string Key, MaterialIconKind Kind)[] Map =
    [
        // Navigation and shell
        ("NutIconOverview", MaterialIconKind.ViewDashboardOutline),
        ("NutIconDevices", MaterialIconKind.BatteryChargingHigh),
        ("NutIconAdministration", MaterialIconKind.ShieldAccountOutline),
        ("NutIconDiagnostics", MaterialIconKind.Pulse),
        ("NutIconSettings", MaterialIconKind.CogOutline),
        ("NutIconMenu", MaterialIconKind.Menu),
        ("NutIconProfile", MaterialIconKind.AccountCircleOutline),

        // Configuration domains, used by the file rail
        ("NutIconGeneral", MaterialIconKind.FileCogOutline),
        ("NutIconUps", MaterialIconKind.BatteryHeartOutline),
        ("NutIconServer", MaterialIconKind.ServerNetwork),
        ("NutIconUsers", MaterialIconKind.AccountGroupOutline),
        ("NutIconMonitoring", MaterialIconKind.MonitorEye),

        // Metrics
        ("NutIconBattery", MaterialIconKind.BatteryOutline),
        ("NutIconGauge", MaterialIconKind.GaugeFull),
        ("NutIconRuntime", MaterialIconKind.TimerSandComplete),
        ("NutIconInput", MaterialIconKind.TransmissionTower),
        ("NutIconOutput", MaterialIconKind.PowerPlugOutline),
        ("NutIconTemperature", MaterialIconKind.ThermometerLines),
        ("NutIconDriver", MaterialIconKind.Chip),
        ("NutIconConnection", MaterialIconKind.LanConnect),

        // Transport and security
        ("NutIconNetwork", MaterialIconKind.Web),
        ("NutIconPort", MaterialIconKind.UsbPort),
        ("NutIconRemote", MaterialIconKind.RemoteDesktop),
        ("NutIconSmb", MaterialIconKind.FolderNetworkOutline),
        ("NutIconFolder", MaterialIconKind.FolderOutline),
        ("NutIconFile", MaterialIconKind.FileDocumentOutline),
        ("NutIconLogs", MaterialIconKind.TextBoxOutline),
        ("NutIconShield", MaterialIconKind.ShieldCheckOutline),
        ("NutIconCertificate", MaterialIconKind.CertificateOutline),
        ("NutIconTls", MaterialIconKind.LockOutline),

        // Service control
        ("NutIconService", MaterialIconKind.ServerOutline),
        ("NutIconStart", MaterialIconKind.PlayOutline),
        ("NutIconStop", MaterialIconKind.StopCircleOutline),
        ("NutIconRestart", MaterialIconKind.Restart),

        // Actions
        ("NutIconAdd", MaterialIconKind.PlusCircleOutline),
        ("NutIconEdit", MaterialIconKind.PencilOutline),
        ("NutIconDelete", MaterialIconKind.TrashCanOutline),
        ("NutIconApply", MaterialIconKind.CheckCircleOutline),
        ("NutIconDiscard", MaterialIconKind.CloseCircleOutline),
        ("NutIconReview", MaterialIconKind.ClipboardCheckOutline),
        ("NutIconPreview", MaterialIconKind.EyeOutline),
        ("NutIconSearch", MaterialIconKind.Magnify),
        ("NutIconCopy", MaterialIconKind.ContentCopy),
        ("NutIconRefresh", MaterialIconKind.Refresh),

        // Feedback
        ("NutIconHelp", MaterialIconKind.HelpCircleOutline),
        ("NutIconInfo", MaterialIconKind.InformationOutline),
        // A disc rather than a hazard triangle, matching NutIconError beside it. The triangle is
        // the road-sign shape for danger, and what this marks is a condition to read.
        ("NutIconWarning", MaterialIconKind.AlertCircleOutline),
        ("NutIconError", MaterialIconKind.AlertCircleOutline),
        ("NutIconSuccess", MaterialIconKind.CheckCircleOutline),
        ("NutIconCheck", MaterialIconKind.Check),

        // Chrome
        ("NutIconChevronLeft", MaterialIconKind.ChevronLeft),
        ("NutIconChevronRight", MaterialIconKind.ChevronRight),
        ("NutIconChevronDown", MaterialIconKind.ChevronDown),
        ("NutIconChevronUp", MaterialIconKind.ChevronUp),
        ("NutIconBack", MaterialIconKind.ArrowLeft),
        ("NutIconForward", MaterialIconKind.ArrowRight),
        ("NutIconClose", MaterialIconKind.Close),
        // Theme toggle. The disc-and-rays sun is one shape here, where it used to be a disc with a
        // separate ray ring so the rays could turn alone; the whole glyph turns now.
        ("NutIconSun", MaterialIconKind.WhiteBalanceSunny),
        ("NutIconMoon", MaterialIconKind.WeatherNight),
        ("NutIconWindowMinimize", MaterialIconKind.WindowMinimize),
        ("NutIconWindowMaximize", MaterialIconKind.WindowMaximize),
        ("NutIconWindowRestore", MaterialIconKind.WindowRestore)
    ];

    /// <summary>
    /// Replaces the catalog entries this library covers. Called after the theme dictionaries have
    /// been composed, so a kind the installed version has dropped falls back to the drawing in
    /// <c>NutIcons.axaml</c> rather than leaving an empty box on screen.
    /// </summary>
    public static void Apply(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var (key, kind) in Map)
        {
            var data = MaterialIconDataProvider.GetData(kind);
            if (string.IsNullOrWhiteSpace(data))
            {
                // A kind the installed version does not carry leaves the existing drawing in place.
                continue;
            }

            application.Resources[key] = Geometry.Parse(data);
        }
    }

    /// <summary>The names this library supplies, for tests that check the split is what it claims.</summary>
    public static IReadOnlyList<string> SuppliedKeys => [.. Map.Select(entry => entry.Key)];
}
