using Avalonia;
using Avalonia.Controls;
using NutManager.App.ViewModels;

namespace NutManager.App.Presentation.Controls;

/// <summary>
/// Renders the layered glyph for one navigation destination. The kind is bound from the navigation
/// item, so the shell never repeats the per-page icon composition.
/// </summary>
public partial class NutNavigationIcon : UserControl
{
    public static readonly StyledProperty<AppPage> KindProperty =
        AvaloniaProperty.Register<NutNavigationIcon, AppPage>(nameof(Kind));

    /// <summary>Set by a style when the row is hovered or selected. Drives the looping motion.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<NutNavigationIcon, bool>(nameof(IsActive));

    public NutNavigationIcon() => InitializeComponent();

    public AppPage Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public bool IsOverview => Kind == AppPage.Overview;
    public bool IsDevices => Kind == AppPage.Devices;
    public bool IsAdministration => Kind == AppPage.Administration;
    public bool IsDiagnostics => Kind == AppPage.Diagnostics;
    public bool IsSettings => Kind == AppPage.Settings;
    public bool IsAbout => Kind == AppPage.About;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyMotion();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            ApplyMotion();
            return;
        }

        if (change.Property != KindProperty) return;
        RaisePropertyChanged(IsOverviewProperty, default, default);
        RaisePropertyChanged(IsDevicesProperty, default, default);
        RaisePropertyChanged(IsAdministrationProperty, default, default);
        RaisePropertyChanged(IsDiagnosticsProperty, default, default);
        RaisePropertyChanged(IsSettingsProperty, default, default);
        RaisePropertyChanged(IsAboutProperty, default, default);
        ApplyMotion();
    }

    /// <summary>
    /// The glyph slot, which is what every movement below is measured against. It matches the fixed
    /// size of the named Panel rather than the ink inside it, so the centre point is the centre of
    /// the box whatever aspect ratio the library's drawing happens to have.
    /// </summary>
    private static readonly Size GlyphSize = new(20, 20);

    /// <summary>
    /// Each destination moves in a way that describes what it opens, rather than the same wiggle
    /// applied five times.
    ///
    /// The library gives one shape per icon, so the movement is the whole glyph rather than a part
    /// of it. What each family means survives that: a dashboard still breathes, hardware still
    /// pulses on a rack-light cadence, an authorization still lands in one pop, a pulse trace still
    /// beats and a cog still turns. Amplitudes are smaller than the per-part ones were, because a
    /// detail can move a long way inside a silhouette that holds still, while a whole icon moving
    /// that far reads as the row itself twitching.
    /// </summary>
    private void ApplyMotion()
    {
        if (!IsActive)
        {
            foreach (var glyph in new Visual[] { OverviewGlyph, DevicesGlyph, AdministrationGlyph, DiagnosticsGlyph, SettingsGlyph, AboutGlyph })
            {
                NutIconMotion.Reset(glyph, restingOpacity: 1);
            }

            return;
        }

        switch (Kind)
        {
            case AppPage.Overview:
                // A dashboard of live figures, breathing calmly.
                NutIconMotion.Breathe(OverviewGlyph, GlyphSize, 1.08, TimeSpan.FromSeconds(1.9));
                break;

            case AppPage.Devices:
                // The rack-light cadence, kept as a soft rise and fall. A hard blink of a whole
                // glyph reads as a rendering fault rather than as hardware reporting in.
                NutIconMotion.Glow(DevicesGlyph, 0.55, 1, TimeSpan.FromSeconds(1.4));
                break;

            case AppPage.Administration:
                // One pop, not a loop: authorization is something that lands, not something that
                // keeps happening. The opacity floor is high so the glyph swells rather than
                // flashing back in, which is what a whole icon does at the detail layer's floor.
                NutIconMotion.PopOnce(AdministrationGlyph, GlyphSize, 1.14, TimeSpan.FromMilliseconds(220), from: 0.85);
                break;

            case AppPage.Diagnostics:
                // A beat, on the faster cadence a pulse trace implies.
                NutIconMotion.Breathe(DiagnosticsGlyph, GlyphSize, 1.12, TimeSpan.FromSeconds(1.15));
                break;

            case AppPage.Settings:
                NutIconMotion.Spin(SettingsGlyph, GlyphSize, TimeSpan.FromSeconds(7));
                break;

            case AppPage.About:
                NutIconMotion.Breathe(AboutGlyph, GlyphSize, 1.08, TimeSpan.FromSeconds(1.9));
                break;
        }
    }

    private static readonly DirectProperty<NutNavigationIcon, bool> IsOverviewProperty =
        AvaloniaProperty.RegisterDirect<NutNavigationIcon, bool>(nameof(IsOverview), owner => owner.IsOverview);
    private static readonly DirectProperty<NutNavigationIcon, bool> IsDevicesProperty =
        AvaloniaProperty.RegisterDirect<NutNavigationIcon, bool>(nameof(IsDevices), owner => owner.IsDevices);
    private static readonly DirectProperty<NutNavigationIcon, bool> IsAdministrationProperty =
        AvaloniaProperty.RegisterDirect<NutNavigationIcon, bool>(nameof(IsAdministration), owner => owner.IsAdministration);
    private static readonly DirectProperty<NutNavigationIcon, bool> IsDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<NutNavigationIcon, bool>(nameof(IsDiagnostics), owner => owner.IsDiagnostics);
    private static readonly DirectProperty<NutNavigationIcon, bool> IsSettingsProperty =
        AvaloniaProperty.RegisterDirect<NutNavigationIcon, bool>(nameof(IsSettings), owner => owner.IsSettings);
    private static readonly DirectProperty<NutNavigationIcon, bool> IsAboutProperty =
        AvaloniaProperty.RegisterDirect<NutNavigationIcon, bool>(nameof(IsAbout), owner => owner.IsAbout);
}
