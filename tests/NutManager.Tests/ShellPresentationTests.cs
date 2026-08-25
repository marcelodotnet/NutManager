using NutManager.App.Localization;
using NutManager.App.ViewModels;
using NutManager.Core.Models;
using Xunit;

namespace NutManager.Tests;

public sealed class ShellPresentationTests
{
    [Fact]
    public void BothOfficialCulturesContainAllRequiredKeys()
    {
        Assert.True(NutManagerLocalizer.HasRequiredKeys(UiLanguagePreference.PtBr));
        Assert.True(NutManagerLocalizer.HasRequiredKeys(UiLanguagePreference.EnUs));
    }

    [Fact]
    public void OfficialCulturesExposeExactlyTheSameSemanticKeys()
    {
        var portuguese = NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.PtBr);
        var english = NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.EnUs);

        Assert.Equal(portuguese.OrderBy(key => key), english.OrderBy(key => key));
        Assert.All(NutManagerLocalizer.RequiredKeys, key => Assert.Contains(key, portuguese));
    }

    [Fact]
    public void MissingResourceFallsBackDeterministicallyToItsKey() =>
        Assert.Equal("Missing.Key", new NutManagerLocalizer(UiLanguagePreference.EnUs).Get("Missing.Key"));

    [Fact]
    public void LocalizedNavigationUsesSemanticResources()
    {
        Assert.Equal("Visão geral", new NutManagerLocalizer(UiLanguagePreference.PtBr).Get("Nav.Overview"));
        Assert.Equal("Overview", new NutManagerLocalizer(UiLanguagePreference.EnUs).Get("Nav.Overview"));
    }

    [Fact]
    public void ProfileQuickMenuResourcesAreLocalizedInBothOfficialCultures()
    {
        var portuguese = new NutManagerLocalizer(UiLanguagePreference.PtBr);
        var english = new NutManagerLocalizer(UiLanguagePreference.EnUs);

        Assert.Equal("Perfis cadastrados", portuguese.Get("Shell.SavedProfiles"));
        Assert.Equal("Saved profiles", english.Get("Shell.SavedProfiles"));
        Assert.Equal("Abrir perfis", portuguese.Get("Shell.OpenProfiles"));
        Assert.Equal("Open profiles", english.Get("Shell.OpenProfiles"));
        Assert.Equal("Gerenciar perfis", portuguese.Get("Shell.ManageProfiles"));
        Assert.Equal("Manage profiles", english.Get("Shell.ManageProfiles"));
        // The authorship line is prose now rather than a symbol and a name, so it translates. It used
        // to be identical in both cultures because "© 2026 · NUT Manager · Marcelo Pacheco" had nothing
        // in it to translate; asserting sameness now would be asserting that it was left untranslated.
        Assert.Equal("Desenvolvido por Marcelo Pacheco", portuguese.Get("Shell.Authorship"));
        Assert.Equal("Developed by Marcelo Pacheco", english.Get("Shell.Authorship"));
        Assert.NotEqual(portuguese.Get("Shell.Authorship"), english.Get("Shell.Authorship"));
    }

    [Fact]
    public void TechnicalNutTokensAreInvariant()
    {
        var portuguese = new NutManagerLocalizer(UiLanguagePreference.PtBr);
        var english = new NutManagerLocalizer(UiLanguagePreference.EnUs);

        Assert.Equal("ups.conf", "ups.conf");
        Assert.Equal("SFTP", "SFTP");
        Assert.Equal("MONITOR", "MONITOR");
        Assert.NotEqual(portuguese.Get("Nav.Settings"), english.Get("Nav.Settings"));
    }

    [Fact]
    public void EveryTypedValidationResourceResolvesInBothOfficialCultures()
    {
        var keys = NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.PtBr)
            .Where(key => key.StartsWith("Validation.", StringComparison.Ordinal))
            .ToArray();
        var portuguese = new NutManagerLocalizer(UiLanguagePreference.PtBr);
        var english = new NutManagerLocalizer(UiLanguagePreference.EnUs);

        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.NotEqual(key, portuguese.Get(key));
            Assert.NotEqual(key, english.Get(key));
        });
    }

    [Fact]
    public void ManagedProfileOptionsUseLocalizedPresentationInsteadOfRawEnumNames()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "Server",
            new NutMonitoringProfile("host"),
            new NutManagementProfile(NutManagementMode.Local),
            ManagedNutServerAccessMode.ReadOnly);
        var profiles = new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]);
        var portuguese = new SettingsPageViewModel(new ApplicationSettings(), null, profiles, null);
        var english = new SettingsPageViewModel(new ApplicationSettings(language: UiLanguagePreference.EnUs), null, profiles, null);

        Assert.Equal("Identidade atual do Windows", portuguese.SmbAuthenticationOptions.Single(option => option.Value == SmbAuthenticationMode.CurrentWindowsIdentity).Title);
        Assert.Equal("Current Windows identity", english.SmbAuthenticationOptions.Single(option => option.Value == SmbAuthenticationMode.CurrentWindowsIdentity).Title);
        Assert.Equal("Remoto", portuguese.ManagementModeOptions.Single(option => option.Value == NutManagementMode.Remote).Title);
        Assert.Equal("Remote", english.ManagementModeOptions.Single(option => option.Value == NutManagementMode.Remote).Title);
        Assert.DoesNotContain("Smb", portuguese.ConfigurationTransportOptions.Select(option => option.Title));
    }

    [Theory]
    [InlineData(1200, ShellLayoutState.Wide)]
    [InlineData(1199, ShellLayoutState.Medium)]
    [InlineData(860, ShellLayoutState.Medium)]
    [InlineData(859, ShellLayoutState.Compact)]
    public void LayoutBreakpointsAreDeterministic(double width, ShellLayoutState expected) =>
        Assert.Equal(expected, ShellPresentationMapper.LayoutFor(width));

    [Fact]
    public void OverlayDoesNotDestroySidebarPreference()
    {
        Assert.Equal(SidebarDisplayState.Overlay, ShellPresentationMapper.SidebarFor(ShellLayoutState.Compact, SidebarPreference.Expanded));
        Assert.Equal(SidebarDisplayState.Expanded, ShellPresentationMapper.SidebarFor(ShellLayoutState.Wide, SidebarPreference.Expanded));
    }

    [Theory]
    [InlineData(ConnectionState.Connected, DataFreshness.Fresh, ConnectionPresentationState.Healthy)]
    [InlineData(ConnectionState.Connecting, DataFreshness.Fresh, ConnectionPresentationState.Pending)]
    [InlineData(ConnectionState.Reconnecting, DataFreshness.Fresh, ConnectionPresentationState.Pending)]
    [InlineData(ConnectionState.Reconnecting, DataFreshness.Unavailable, ConnectionPresentationState.Pending)]
    [InlineData(ConnectionState.Connected, DataFreshness.Stale, ConnectionPresentationState.Warning)]
    [InlineData(ConnectionState.Disconnected, DataFreshness.Fresh, ConnectionPresentationState.Critical)]
    [InlineData(ConnectionState.ConnectionFailed, DataFreshness.Fresh, ConnectionPresentationState.Critical)]
    [InlineData(ConnectionState.ConnectionFailed, DataFreshness.Unavailable, ConnectionPresentationState.Critical)]
    [InlineData(ConnectionState.Disconnected, DataFreshness.Unavailable, ConnectionPresentationState.Critical)]
    public void ConnectionStateMapsToSemanticPresentation(ConnectionState state, DataFreshness freshness, ConnectionPresentationState expected) =>
        Assert.Equal(expected, ShellPresentationMapper.ConnectionFor(state, freshness, true));

    [Fact]
    public void MissingContextIsAlwaysUnavailable() =>
        Assert.Equal(ConnectionPresentationState.Unavailable, ShellPresentationMapper.ConnectionFor(ConnectionState.Connected, DataFreshness.Fresh, false));

    [Fact]
    public void ReviewDrawerIsHiddenWithoutContext() =>
        Assert.Equal(ReviewDrawerDisplayState.Hidden, ShellPresentationMapper.ReviewFor(ShellLayoutState.Wide, false, true));

    [Fact]
    public void ReviewDrawerUsesWideSpaceOrOverlayAccordingToLayout()
    {
        Assert.Equal(ReviewDrawerDisplayState.Expanded, ShellPresentationMapper.ReviewFor(ShellLayoutState.Wide, true, true));
        Assert.Equal(ReviewDrawerDisplayState.Collapsed, ShellPresentationMapper.ReviewFor(ShellLayoutState.Wide, true, false));
        Assert.Equal(ReviewDrawerDisplayState.Overlay, ShellPresentationMapper.ReviewFor(ShellLayoutState.Medium, true, false));
        Assert.Equal(ReviewDrawerDisplayState.Overlay, ShellPresentationMapper.ReviewFor(ShellLayoutState.Compact, true, true));
    }
}
