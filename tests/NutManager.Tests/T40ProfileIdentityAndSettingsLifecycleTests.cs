using System.Globalization;
using NutManager.App.Localization;
using NutManager.App.ViewModels;
using NutManager.Core.Models;
using NutManager.Core.Services;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// Two corrections about state that outlived the moment it belonged to: a server name captured at
/// startup and never revisited, and a Settings draft that survived the operator walking away from it.
/// </summary>
public sealed class T40ProfileIdentityAndSettingsLifecycleTests
{
    // ---------------------------------------------------------------- runtime profile identity

    [Fact]
    public void RenamingTheRunningProfileRefreshesEverySurfaceThatSpellsItOut()
    {
        var overview = new OverviewPageViewModel();
        var profile = Profile("GANDALF");
        var shell = Shell(overview, profile);

        Assert.Equal("GANDALF", shell.ActiveProfileName);
        Assert.StartsWith("GANDALF · ", shell.FooterServerStatusText, StringComparison.Ordinal);

        shell.ApplyActiveProfileIdentity(Renamed(profile, "SERVIDOR-NUT"));

        // No restart, no reconstruction: the same instance now answers with the new name.
        Assert.Equal("SERVIDOR-NUT", shell.ActiveProfileName);
        Assert.StartsWith("SERVIDOR-NUT · ", shell.FooterServerStatusText, StringComparison.Ordinal);
        Assert.StartsWith("SERVIDOR-NUT · ", shell.FooterServerAccessibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("GANDALF", shell.FooterServerStatusText, StringComparison.Ordinal);

        // The Overview reads its own copy of the profile rather than the shell property, which is how
        // the dashboard could keep the old name even once the footer had been corrected.
        Assert.Contains(
            overview.ActiveProfileRows,
            row => string.Equals(row.Value, "SERVIDOR-NUT", StringComparison.Ordinal));
        Assert.DoesNotContain(
            overview.ActiveProfileRows,
            row => string.Equals(row.Value, "GANDALF", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRenameRaisesTheNotificationsTheBoundSurfacesListenFor()
    {
        var profile = Profile("GANDALF");
        var shell = Shell(new OverviewPageViewModel(), profile);
        var notified = new List<string>();
        shell.PropertyChanged += (_, args) => notified.Add(args.PropertyName ?? string.Empty);

        shell.ApplyActiveProfileIdentity(Renamed(profile, "SERVIDOR-NUT"));

        // A correct value nobody is told about is the same bug in a different place: the sidebar card
        // binds ActiveProfileName, and the footer binds both of its readings.
        Assert.Contains(nameof(MainWindowViewModel.ActiveProfileName), notified);
        Assert.Contains(nameof(MainWindowViewModel.FooterServerStatusText), notified);
        Assert.Contains(nameof(MainWindowViewModel.FooterServerAccessibleText), notified);
    }

    [Fact]
    public void RenamingSomeOtherSavedProfileLeavesTheRunningOneAlone()
    {
        var profile = Profile("GANDALF");
        var shell = Shell(new OverviewPageViewModel(), profile);

        shell.ApplyActiveProfileIdentity(Profile("OUTRO-SERVIDOR"));

        Assert.Equal("GANDALF", shell.ActiveProfileName);
    }

    [Fact]
    public void ARenameCarriesTheNameAndNothingElseAcrossIntoTheLiveSession()
    {
        // The guarantee that keeps this from becoming a hot reload. The persisted profile arrives with
        // a different host, port, preferred UPS, transport, directory and access mode; only the name
        // is allowed through, because everything else is what the session actually connected with.
        var overview = new OverviewPageViewModel();
        var original = Profile("GANDALF");
        var shell = Shell(overview, original);

        var replaced = new ManagedNutServerProfile(
            original.Id,
            "SERVIDOR-NUT",
            new NutMonitoringProfile("outro-host.example", 9999, "OUTRO-UPS"),
            new NutManagementProfile(
                NutManagementMode.Remote,
                "outro-gerenciamento.example",
                "/opt/outro",
                configurationTransport: RemoteConfigurationTransportKind.Smb,
                smbSharePath: @"\\OUTRO-SERVIDOR\etc"),
            ManagedNutServerAccessMode.ReadOnly);

        shell.ApplyActiveProfileIdentity(replaced);

        Assert.Equal("SERVIDOR-NUT", shell.ActiveProfileName);

        var values = overview.ActiveProfileRows.Select(row => row.Value).ToArray();
        Assert.DoesNotContain("OUTRO-UPS", values);
        Assert.Contains("UPS-01", values);

        // Access mode has its own narrow path and its own authorization rules; a rename must not be
        // the door it walks through. Manage was the running value and Manage it stays.
        Assert.Contains(shell.Localizer.Get("Access.Manage"), values);
        Assert.DoesNotContain(shell.Localizer.Get("Access.ReadOnly"), values);

        var connectivity = overview.ActiveConnectivityRows.Select(row => row.Value).ToArray();
        Assert.Contains(shell.Localizer.Get("Transport.Sftp"), connectivity);
        Assert.DoesNotContain(shell.Localizer.Get("Transport.Smb"), connectivity);
    }

    [Fact]
    public void OnlyAConfirmedPersistedProfileReachesTheShell()
    {
        // Wiring assertion. The handler is a lambda inside application startup and cannot be called
        // from a test, so what is checked is that the refresh sits behind the runtime-identifier guard
        // and is fed by ProfilePersisted, which fires only after a successful write.
        var startup = Repository.Read(Path.Combine("src", "NutManager.App", "App.axaml.cs"));

        var handler = startup.IndexOf("settingsPage.ProfilePersisted += async profile =>", StringComparison.Ordinal);
        Assert.True(handler > 0, "The persisted-profile handler is gone or was renamed.");

        var guard = startup.IndexOf("if (profile.Id != runtimeProfile.Profile.Id) return;", handler, StringComparison.Ordinal);
        var refresh = startup.IndexOf("viewModel.ApplyActiveProfileIdentity(profile);", handler, StringComparison.Ordinal);

        Assert.True(guard > 0, "The runtime-profile guard is gone.");
        Assert.True(refresh > guard, "The identity refresh must sit behind the runtime-profile guard.");
    }

    // ---------------------------------------------------------------- leaving settings

    [Fact]
    public void LeavingSettingsWithAnUnsavedRenameDiscardsItWithoutWriting()
    {
        var profile = Profile("GANDALF");
        var store = new CountingProfileStore(Profiles(profile));
        var settings = Settings(store, profile);

        settings.ProfileDraft.Name = "RASCUNHO-NAO-SALVO";
        settings.PollingIntervalSeconds = "42";
        settings.ConnectionTimeoutSeconds = "17";
        Assert.True(settings.IsProfileDraftDirty);

        settings.OnDeactivated();

        Assert.Equal(0, store.SaveCalls);
        Assert.Equal("GANDALF", settings.ProfileDraft.Name);
        Assert.False(settings.IsProfileDraftDirty);
        Assert.False(settings.CanSaveAll);

        var confirmed = new ApplicationSettings();
        Assert.Equal(Seconds(confirmed.PollingInterval), settings.PollingIntervalSeconds);
        Assert.Equal(Seconds(confirmed.ConnectionTimeout), settings.ConnectionTimeoutSeconds);
    }

    [Fact]
    public void LeavingSettingsClearsTheDirtyDraftQuestionAndTheAbandonedTestResult()
    {
        var profile = Profile("GANDALF");
        var other = Profile("OUTRO");
        var store = new CountingProfileStore(Profiles(profile, other));
        var settings = Settings(store, profile);

        settings.ProfileDraft.Name = "RASCUNHO-NAO-SALVO";

        // Selecting another profile with a dirty draft raises the Save / Discard / Continue question.
        // That protection is for actions taken inside Settings and stays exactly as it was.
        settings.SelectedManagedProfile = other;
        Assert.True(settings.IsDirtyDraftDecisionVisible);

        settings.ConnectionTestResultText = "Conectado a rascunho.example:3493";
        settings.ProfileStatusMessage = settings.Localizer.Get("Profiles.NewServerHelp");

        settings.OnDeactivated();

        // The question was about an action that is no longer going to happen.
        Assert.False(settings.IsDirtyDraftDecisionVisible);

        // And the verdict was about a host that is no longer on screen.
        Assert.False(settings.HasConnectionTestResult);
        Assert.Null(settings.ConnectionTestStatus);
        Assert.False(settings.HasProfileStatusMessage);
        Assert.False(settings.IsProfileDraftDirty);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public void LeavingSettingsAbandonsAHalfCreatedServerWithoutPersistingIt()
    {
        var profile = Profile("GANDALF");
        var store = new CountingProfileStore(Profiles(profile));
        var settings = Settings(store, profile);

        settings.NewServerCommand.Execute(null);
        settings.ProfileDraft.Name = "SERVIDOR-INACABADO";
        settings.ProfileDraft.MonitoringHost = "inacabado.example";

        Assert.True(settings.IsCreatingProfile);
        Assert.True(settings.IsProfileDraftDirty);

        settings.OnDeactivated();

        // Never written, and the page is back on a profile that actually exists.
        Assert.Equal(0, store.SaveCalls);
        Assert.False(settings.IsCreatingProfile);
        Assert.False(settings.IsProfileDraftDirty);
        Assert.Equal("GANDALF", settings.ProfileDraft.Name);
        Assert.Equal(profile.Id, settings.SelectedManagedProfile?.Id);
        Assert.DoesNotContain(
            settings.ManagedProfiles,
            candidate => string.Equals(candidate.Name, "SERVIDOR-INACABADO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavingSettingsDoesNotWalkBackPreferencesThatWereAlreadyPersisted()
    {
        // Theme, language, sidebar and transparency are written the moment they change; they were
        // never waiting on Save. Restoring the two fields that *are* pending must not drag them along,
        // which is what reading from a stale confirmed copy would have done.
        var profile = Profile("GANDALF");
        var store = new CountingProfileStore(Profiles(profile));
        var settingsStore = new CountingSettingsStore();
        var settings = new SettingsPageViewModel(
            new ApplicationSettings(),
            settingsStore,
            Profiles(profile),
            store,
            runtimeProfileId: profile.Id);

        await settings.PersistThemeAsync(ThemePreference.Dark);
        await settings.PersistVisualPreferencesAsync(UiLanguagePreference.EnUs, SidebarPreference.Collapsed);

        settings.ProfileDraft.Name = "RASCUNHO-NAO-SALVO";
        settings.PollingIntervalSeconds = "42";

        settings.OnDeactivated();

        Assert.Equal(ThemePreference.Dark, settingsStore.Last?.Theme);
        Assert.Equal(UiLanguagePreference.EnUs, settingsStore.Last?.Language);
        Assert.Equal(SidebarPreference.Collapsed, settingsStore.Last?.SidebarPreference);

        // No further write happened on the way out - leaving is not a save.
        Assert.Equal(2, settingsStore.SaveCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public void LeavingSettingsKeepsAPersistedLoadErrorAndTheProfilesThemselves()
    {
        var profile = Profile("GANDALF");
        var other = Profile("OUTRO");
        var store = new CountingProfileStore(Profiles(profile, other));
        var settings = Settings(store, profile);

        settings.SetProfileLoadError("Não foi possível ler managed-servers.json.");
        settings.ProfileDraft.Name = "RASCUNHO-NAO-SALVO";

        settings.OnDeactivated();

        // A load error describes the state of the world, not of a draft. Tidying it away on the way
        // out would hide a problem rather than a banner.
        Assert.True(settings.HasProfileLoadError);
        Assert.Equal(2, settings.ManagedProfiles.Count);
        Assert.Equal(0, store.SaveCalls);
    }

    // ---------------------------------------------------------------- helpers

    private static MainWindowViewModel Shell(OverviewPageViewModel overview, ManagedNutServerProfile profile) =>
        new(ThemePreference.Dark,
            overview,
            new DevicesPageViewModel(),
            activeProfileName: profile.Name,
            managementMode: profile.Management.Mode,
            accessMode: profile.AccessMode,
            preferredUpsName: profile.Monitoring.PreferredUpsName,
            activeProfile: profile);

    private static SettingsPageViewModel Settings(
        CountingProfileStore store,
        ManagedNutServerProfile runtimeProfile) =>
        new(new ApplicationSettings(),
            null,
            store.Current,
            store,
            runtimeProfileId: runtimeProfile.Id);

    private static ManagedNutServerProfiles Profiles(params ManagedNutServerProfile[] profiles) =>
        new(ManagedNutServerProfiles.CurrentSchemaVersion, profiles[0].Id, profiles);

    private static ManagedNutServerProfile Profile(string name) => new(
        Guid.NewGuid(),
        name,
        new NutMonitoringProfile("monitor.example", 3493, "UPS-01"),
        new NutManagementProfile(NutManagementMode.Remote, "management.example", "/etc/nut"),
        ManagedNutServerAccessMode.Manage);

    private static ManagedNutServerProfile Renamed(ManagedNutServerProfile profile, string name) => new(
        profile.Id, name, profile.Monitoring, profile.Management, profile.AccessMode);

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed class CountingProfileStore : IManagedNutServerProfileStore
    {
        public CountingProfileStore(ManagedNutServerProfiles current) => Current = current;

        public ManagedNutServerProfiles Current { get; private set; }

        public int SaveCalls { get; private set; }

        public Task<ManagedNutServerProfiles?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ManagedNutServerProfiles?>(Current);

        public Task SaveAsync(ManagedNutServerProfiles profiles, CancellationToken cancellationToken)
        {
            SaveCalls++;
            Current = profiles;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingSettingsStore : IApplicationSettingsStore
    {
        public ApplicationSettings? Last { get; private set; }

        public int SaveCalls { get; private set; }

        public Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ApplicationSettings());

        public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
        {
            SaveCalls++;
            Last = settings;
            return Task.CompletedTask;
        }
    }
}
