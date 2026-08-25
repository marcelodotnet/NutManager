using NutManager.App.Localization;
using NutManager.Core.Administration;
using NutManager.App.Services;
using NutManager.App.ViewModels;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// The presentation and lifecycle corrections made before the T39 installer acceptance.
///
/// Most of these are about state living longer than the moment it belonged to — a confirmation that
/// outlived the screen, a banner that outlived its action, an agent reading that outlived the agent.
/// </summary>
public sealed class T39PresentationLifecycleTests
{
    // ---------------------------------------------------------------- confirmation lifecycle

    [Fact]
    public async Task DiscardingAPendingStopSendsNothingToTheAgent()
    {
        // The point of clearing a confirmation on navigation is that it is only a question. If
        // discarding it could reach the agent, walking away from the screen would become a way to stop
        // a service by accident, which is the opposite of what asking first is for.
        var client = new CountingAgentClient(NutServiceState.Running);
        await using var monitor = new RemoteWindowsServiceViewModel("gandalf.example.local", client);
        var control = new RemoteWindowsServiceControlViewModel(monitor, client);
        await monitor.RefreshAsync();

        control.Stop();
        Assert.True(control.IsConfirming);

        var before = client.ControlCalls;
        control.CancelConfirmation();

        Assert.False(control.IsConfirming);
        Assert.Equal(before, client.ControlCalls);
    }

    [Fact]
    public async Task DiscardingAPendingRestartSendsNothingToTheAgent()
    {
        var client = new CountingAgentClient(NutServiceState.Running);
        await using var monitor = new RemoteWindowsServiceViewModel("gandalf.example.local", client);
        var control = new RemoteWindowsServiceControlViewModel(monitor, client);
        await monitor.RefreshAsync();

        control.Restart();
        Assert.True(control.IsConfirming);

        control.CancelConfirmation();

        Assert.False(control.IsConfirming);
        Assert.Equal(0, client.ControlCalls);
    }

    [Fact]
    public void AdministrationDiscardsConfirmationsOnLeavingAndOnSwitchingSection()
    {
        // Wiring assertion. The administration page needs a full runtime to construct, so what is
        // checked here is that both exits route through the same discard: leaving the page, and moving
        // to another section within it. Losing either one restores the bug.
        var source = Source("src", "NutManager.App", "ViewModels", "AdministrationPageViewModel.cs");

        Assert.Contains("public override void OnDeactivated() => DiscardPendingConfirmations();", source, StringComparison.Ordinal);

        var sectionChanged = source.IndexOf("partial void OnSelectedAdministrationSectionChanged", StringComparison.Ordinal);
        Assert.True(sectionChanged > 0, "The section-changed handler is gone or was renamed.");
        Assert.Contains("DiscardPendingConfirmations();", source[sectionChanged..], StringComparison.Ordinal);

        // And the discard reaches both kinds of confirmation: the remote agent's and the local one.
        Assert.Contains("RemoteWindowsServiceControl?.CancelConfirmation();", source, StringComparison.Ordinal);
        Assert.Contains("InvalidateAdministrativeAction();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationTellsThePageItIsLeaving()
    {
        var source = Source("src", "NutManager.App", "ViewModels", "MainWindowViewModel.cs");

        var navigate = source.IndexOf("private void Navigate(AppPage page)", StringComparison.Ordinal);
        Assert.True(navigate > 0);

        var deactivate = source.IndexOf("CurrentPage?.OnDeactivated();", navigate, StringComparison.Ordinal);
        var assign = source.IndexOf("CurrentPage = _pages[page];", navigate, StringComparison.Ordinal);

        Assert.True(deactivate > 0, "Navigation no longer notifies the outgoing page.");
        Assert.True(deactivate < assign, "The page must be told before it is replaced, not after.");
    }

    // ---------------------------------------------------------------- transient settings feedback

    [Fact]
    public void LeavingSettingsClearsBothSuccessBannersButNotAFailure()
    {
        // Two separate channels, and clearing only one is how "Configurações salvas." survived a visit:
        // the general-settings banner is a flag, the profile banner is a message, and they are rendered
        // by different bindings.
        var settings = new SettingsPageViewModel(new ApplicationSettings(), null);

        settings.IsSaved = true;
        settings.ProfileStatusMessage = settings.Localizer.Get("Profiles.SaveSuccess");

        settings.OnDeactivated();

        Assert.False(settings.IsSaved);
        Assert.False(settings.HasProfileStatusMessage);

        // A failure is not feedback for a finished action; it is a problem nobody has dealt with, and
        // tidying it away on navigation would hide it.
        const string failure = "A gravação falhou: acesso negado.";
        settings.ProfileStatusMessage = failure;
        settings.OnDeactivated();

        Assert.Equal(failure, settings.ProfileStatusMessage);
    }

    [Fact]
    public void SaveIsOfferedOnlyWhenSomethingIsUnsaved()
    {
        // An enabled button claims that pressing it does something. Offered on an untouched page, it
        // rewrote the settings file with its own contents and reported success — harmless, and enough
        // to teach an operator that the success message means nothing.
        var settings = new SettingsPageViewModel(new ApplicationSettings(), null);

        Assert.False(settings.CanSaveAll);

        settings.PollingIntervalSeconds = "9";
        Assert.True(settings.CanSaveAll);

        // Back to the confirmed value: nothing to save again.
        settings.PollingIntervalSeconds = new ApplicationSettings().PollingInterval
            .TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(settings.CanSaveAll);
    }

    [Fact]
    public void TheSavedBannerCarriesACheckInBothCultures()
    {
        foreach (var language in new[] { UiLanguagePreference.PtBr, UiLanguagePreference.EnUs })
        {
            Assert.StartsWith("✅", new NutManagerLocalizer(language).Get("Settings.SaveSuccess"), StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- version presentation

    [Theory]
    [InlineData("1.0.0+adc0fe9399c4b4ba39b05349a72b04a8819840aa", "v1.0.0")]
    [InlineData("1.0.0", "v1.0.0")]
    [InlineData("2.13.4", "v2.13.4")]
    [InlineData("v1.0.0", "v1.0.0")]
    public void TheDisplayVersionDropsBuildMetadataAndGainsAPrefix(string informational, string expected) =>
        Assert.Equal(expected, ApplicationRuntimeInfo.FormatDisplayVersion(informational));

    [Fact]
    public void APreReleaseSuffixSurvivesTheTrim()
    {
        // Everything after "+" is build metadata and is noise on screen. Everything after "-" is the
        // pre-release identity, and a user running a release candidate needs to know it.
        Assert.Equal("v1.1.0-rc.1", ApplicationRuntimeInfo.FormatDisplayVersion("1.1.0-rc.1+deadbee"));
        Assert.Equal("v1.1.0-rc.1", ApplicationRuntimeInfo.FormatDisplayVersion("1.1.0-rc.1"));
    }

    [Fact]
    public void TheTechnicalVersionIsKeptAlongsideTheDisplayOne()
    {
        var info = ApplicationRuntimeInfo.CreateCurrent();

        Assert.StartsWith("v", info.DisplayVersion, StringComparison.Ordinal);
        Assert.DoesNotContain("+", info.DisplayVersion, StringComparison.Ordinal);

        // The full one is not thrown away — the copied diagnostic report is where a build gets
        // identified, and that is worth more than a tidy field.
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    // ---------------------------------------------------------------- agent rebinding

    [Fact]
    public async Task RebindingTheAgentSwapsTheClientAndReprobes()
    {
        var original = new CountingAgentClient(NutServiceState.Running);
        var replacement = new CountingAgentClient(NutServiceState.Stopped);

        await using var monitor = new RemoteWindowsServiceViewModel(
            "gandalf.example.local", original, transport: NutAgentTransportKind.NamedPipe);

        await monitor.RefreshAsync();
        Assert.Equal(NutAgentTransportKind.NamedPipe, monitor.Transport);
        var originalProbes = original.Probes;

        await monitor.RebindAsync(replacement, NutAgentTransportKind.Https);

        Assert.Equal(NutAgentTransportKind.Https, monitor.Transport);
        Assert.True(replacement.Probes > 0, "The new client was never asked anything.");
        Assert.Equal(originalProbes, original.Probes);
    }

    [Fact]
    public async Task RebindingToTheSameClientAndTransportDoesNothing()
    {
        // Saving an unrelated profile field must not tear down a working agent connection.
        var client = new CountingAgentClient(NutServiceState.Running);
        await using var monitor = new RemoteWindowsServiceViewModel(
            "gandalf.example.local", client, transport: NutAgentTransportKind.NamedPipe);

        await monitor.RefreshAsync();
        var probes = client.Probes;

        await monitor.RebindAsync(client, NutAgentTransportKind.NamedPipe);

        Assert.Equal(probes, client.Probes);
    }

    [Fact]
    public async Task RebindingDropsAConfirmationRaisedAgainstTheOldConnection()
    {
        // The question was asked about a transport that no longer exists. Answering it would send an
        // operation somewhere the operator never agreed to.
        var original = new CountingAgentClient(NutServiceState.Running);
        var replacement = new CountingAgentClient(NutServiceState.Running);

        await using var monitor = new RemoteWindowsServiceViewModel("gandalf.example.local", original);
        var control = new RemoteWindowsServiceControlViewModel(monitor, original);
        await monitor.RefreshAsync();

        control.Stop();
        Assert.True(control.IsConfirming);

        control.Rebind(replacement);

        Assert.False(control.IsConfirming);
        Assert.Equal(0, replacement.ControlCalls);
    }

    [Fact]
    public void AgentSettingsAreRebuiltOnSaveWithoutRecreatingTheConfigurationTransport()
    {
        var app = Source("src", "NutManager.App", "App.axaml.cs");

        // Rebuilt only when the agent settings actually changed.
        Assert.Contains("if (updated == agentSettings) return;", app, StringComparison.Ordinal);

        var handler = app.IndexOf("settingsPage.ProfilePersisted +=", StringComparison.Ordinal);
        Assert.True(handler > 0);

        // Bounded to the handler itself. Startup does connect the configuration transport a few lines
        // further down, and scanning past the closing brace would read that as this handler's doing.
        var close = app.IndexOf("        };", handler, StringComparison.Ordinal);
        Assert.True(close > handler, "The profile-persisted handler no longer closes where expected.");
        var body = app[handler..close];

        // Through the same factory as startup, so the credential rules are the reviewed ones.
        Assert.Contains("NutAgentClientFactory.CreateAsync", body, StringComparison.Ordinal);
        Assert.Contains("RebindAsync(rebuilt, updated.Transport)", body, StringComparison.Ordinal);

        // And nothing about the configuration transport or the NUT session is rebuilt here. Recreating
        // either would turn a narrow agent change into a hot reload of the whole profile.
        foreach (var forbidden in new[] { "new NutConfigurationFilePipeline(", "new UpsPollingCoordinator(", "TryConnectAndValidate" })
        {
            Assert.False(
                body.Contains(forbidden, StringComparison.Ordinal),
                $"The profile-persisted handler rebuilds '{forbidden}'. Only the agent may be rebound here.");
        }
    }

    [Fact]
    public void AgentPollingBelongsToTheApplicationRatherThanToOnePanel()
    {
        // Bound to the Windows service view, the agent reading was whatever the first handshake
        // produced and never moved for anyone who did not open that panel.
        var app = Source("src", "NutManager.App", "App.axaml.cs");
        Assert.Contains("remoteWindowsService?.StartMonitoring();", app, StringComparison.Ordinal);

        // Still one monitor for the process. A second instance would mean a second timer and a second
        // state machine reporting on the same agent.
        Assert.Equal(1, app.Split("new RemoteWindowsServiceViewModel(", StringSplitOptions.None).Length - 1);
    }

    // ---------------------------------------------------------------- footer

    [Fact]
    public void TheFooterCreditsTheDeveloperInBothCultures()
    {
        Assert.Equal(
            "Desenvolvido por Marcelo Pacheco",
            new NutManagerLocalizer(UiLanguagePreference.PtBr).Get("Shell.Authorship"));
        Assert.Equal(
            "Developed by Marcelo Pacheco",
            new NutManagerLocalizer(UiLanguagePreference.EnUs).Get("Shell.Authorship"));
    }

    [Fact]
    public void TheFooterStatusNamesTheServerAndPutsTheDotLast()
    {
        var shell = Source("src", "NutManager.App", "MainWindow.axaml");

        var status = shell.IndexOf("{Binding FooterServerStatusText}", StringComparison.Ordinal);
        Assert.True(status > 0, "The footer no longer shows the server status text.");

        // The dot follows the text, so the line ends on the signal rather than opening with it.
        var firstDot = shell.IndexOf("IsVisible=\"{Binding IsConnectionHealthy}\"", status, StringComparison.Ordinal);
        Assert.True(firstDot > status, "The status dot still precedes the text.");

        // Never colour alone: the exact state reaches assistive technology by name.
        Assert.Contains("AutomationProperties.Name=\"{Binding FooterServerAccessibleText}\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlineAndOfflineExistInBothCultures()
    {
        foreach (var key in new[] { "Shell.ServerOnline", "Shell.ServerOffline" })
        {
            Assert.False(string.IsNullOrWhiteSpace(new NutManagerLocalizer(UiLanguagePreference.PtBr).Get(key)));
            Assert.False(string.IsNullOrWhiteSpace(new NutManagerLocalizer(UiLanguagePreference.EnUs).Get(key)));
        }
    }

    [Fact]
    public void ReconnectingIsNotReportedAsOffline()
    {
        // Calling a retry "offline" sends somebody to the server room for something that is already
        // fixing itself. The amber state gets its own word.
        var shell = Source("src", "NutManager.App", "ViewModels", "MainWindowViewModel.cs");

        var footer = shell.IndexOf("public string FooterServerStatusText", StringComparison.Ordinal);
        Assert.True(footer > 0);

        var body = shell[footer..(footer + 900)];
        Assert.Contains("IsConnectionPending", body, StringComparison.Ordinal);
        Assert.Contains("Status.Reconnecting", body, StringComparison.Ordinal);

        // All three readings exist in both cultures.
        foreach (var language in new[] { UiLanguagePreference.PtBr, UiLanguagePreference.EnUs })
        {
            var strings = new NutManagerLocalizer(language);
            foreach (var key in new[] { "Shell.ServerOnline", "Shell.ServerOffline", "Status.Reconnecting" })
            {
                Assert.False(string.IsNullOrWhiteSpace(strings.Get(key)));
            }
        }

        Assert.Equal("Reconectando", new NutManagerLocalizer(UiLanguagePreference.PtBr).Get("Status.Reconnecting"));
        Assert.Equal("Reconnecting", new NutManagerLocalizer(UiLanguagePreference.EnUs).Get("Status.Reconnecting"));
    }

    [Fact]
    public void TheFooterReadsTheNutConnectionAndNotTheAgent()
    {
        // The three connectivity domains stay apart. A stopped agent must not be able to make a healthy
        // monitoring session read as offline, and a healthy one must not vouch for the agent.
        var shell = Source("src", "NutManager.App", "ViewModels", "MainWindowViewModel.cs");

        var footer = shell.IndexOf("public string FooterServerStatusText", StringComparison.Ordinal);
        Assert.True(footer > 0);

        var expression = shell[footer..(footer + 900)];
        Assert.Contains("IsConnectionHealthy", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent", expression, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- access mode

    [Fact]
    public void ChangingAccessModeTakesEffectWithoutARestart()
    {
        // Every surface reporting access derives it from a profile copy taken at startup, so the
        // interface went on claiming Manage after the profile had been saved read-only. A stale claim
        // about authorization is the worst kind to leave standing.
        var page = new AdministrationPageViewModel(
            null, null, null, null, RemoteContext(ManagedNutServerAccessMode.Manage),
            null, UiLanguagePreference.PtBr, null, null, null, null);

        var manageSections = page.AdministrationSections.Count;
        var manageText = page.AccessModeDisplayText;

        page.ApplyAccessMode(ManagedNutServerAccessMode.ReadOnly);

        Assert.NotEqual(manageText, page.AccessModeDisplayText);
        Assert.True(
            page.AdministrationSections.Count <= manageSections,
            "Narrowing to read-only must not offer more sections than Manage did.");

        // And back again, so the change is not one-way.
        page.ApplyAccessMode(ManagedNutServerAccessMode.Manage);
        Assert.Equal(manageText, page.AccessModeDisplayText);
        Assert.Equal(manageSections, page.AdministrationSections.Count);
    }

    [Fact]
    public void TheSelectedSectionSurvivesAnAccessModeChangeWhenItStillExists()
    {
        // The section list is rebuilt rather than edited, so every item is a new object. Carrying the
        // selection by identity would silently drop the operator back to the first section.
        var page = new AdministrationPageViewModel(
            null, null, null, null, RemoteContext(ManagedNutServerAccessMode.Manage),
            null, UiLanguagePreference.PtBr, null, null, null, null);

        var chosen = page.AdministrationSections[^1].Section;
        page.SelectedAdministrationSection = page.AdministrationSections[^1];

        page.ApplyAccessMode(ManagedNutServerAccessMode.ReadOnly);

        if (page.AdministrationSections.Any(section => section.Section == chosen))
        {
            Assert.Equal(chosen, page.SelectedAdministrationSection.Section);
        }
        else
        {
            // Removed by the narrower mode: falling back to the first is correct, and leaving the
            // selection pointing at a section that is no longer offered would not be.
            Assert.Equal(page.AdministrationSections[0].Section, page.SelectedAdministrationSection.Section);
        }
    }

    [Fact]
    public void WideningToManageGrantsNoWriteWithoutTheSafeWriteProbe()
    {
        // The T19 boundary. Applying Manage at runtime must not become a way around the probe that a
        // remote session performs before anything may be written.
        var app = Source("src", "NutManager.App", "App.axaml.cs");
        var handler = app.IndexOf("settingsPage.ProfilePersisted +=", StringComparison.Ordinal);
        var close = app.IndexOf("        };", handler, StringComparison.Ordinal);
        var body = app[handler..close];

        Assert.Contains("ApplyAccessMode(profile.AccessMode)", body, StringComparison.Ordinal);

        // Nothing here probes, authorizes or marks a capability verified.
        foreach (var forbidden in new[] { "ProbeWrite", "CanEditConfiguration =", "IsWriteCapabilityUnverified" })
        {
            Assert.False(
                body.Contains(forbidden, StringComparison.Ordinal),
                $"The handler touches '{forbidden}'. Access mode is declared intent, not verified capability.");
        }

        // The gate itself still requires the probe.
        var administration = Source("src", "NutManager.App", "ViewModels", "AdministrationPageViewModel.cs");
        Assert.Contains("IsWriteCapabilityUnverified: true", administration, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowingToReadOnlyRevokesWriteAuthorization()
    {
        // The defect this exists for: switching a profile to read-only left the write capability
        // granted until restart. The header said read-only while the session went on reporting the
        // write authorization as still in force, and the two disagreed about the same profile.
        //
        // The session is what decides, because CanEditConfiguration requires the profile to say Manage
        // and the copy it holds was taken at startup.
        var session = Source("src", "NutManager.App", "ViewModels", "RemoteManagementSessionViewModel.cs");

        var gate = session.IndexOf("public bool CanEditConfiguration", StringComparison.Ordinal);
        Assert.True(gate > 0);
        Assert.Contains(
            "_profile.AccessMode == ManagedNutServerAccessMode.Manage",
            session[gate..(gate + 300)],
            StringComparison.Ordinal);

        // And that copy is now updated when the profile is saved.
        Assert.Contains("public void ApplyAccessMode(ManagedNutServerAccessMode accessMode)", session, StringComparison.Ordinal);
        Assert.Contains("OnPropertyChanged(nameof(CanEditConfiguration));", session, StringComparison.Ordinal);

        // The probe's result is not touched, so widening still grants nothing on its own.
        var apply = session.IndexOf("public void ApplyAccessMode", StringComparison.Ordinal);
        var body = session[apply..(apply + 700)];
        Assert.DoesNotContain("WriteCapability =", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatusMessageFromThePreviousAccessModeIsDropped()
    {
        // The state panel shows the last directory validation's message, and that validation ran under
        // the previous access mode — after switching to Manage the panel went on saying the profile is
        // configured read-only, beside a header already saying otherwise. A sentence that is untrue is
        // worse than none, because the reader cannot tell it describes a past state.
        var session = Source("src", "NutManager.App", "ViewModels", "RemoteManagementSessionViewModel.cs");

        var apply = session.IndexOf("public void ApplyAccessMode", StringComparison.Ordinal);
        Assert.True(apply > 0);

        var body = session[apply..(apply + 2800)];
        Assert.Contains("StatusMessage = null;", body, StringComparison.Ordinal);

        // And nothing re-validates. Reaching the remote host as a side effect of saving a settings form
        // is not something a save should do.
        foreach (var forbidden in new[] { "ValidateDirectoryAsync", "ConnectAsync", "ProbeWriteCapabilityAsync" })
        {
            Assert.False(
                body.Contains(forbidden, StringComparison.Ordinal),
                $"ApplyAccessMode calls '{forbidden}'. Saving a form must not reach the remote host.");
        }
    }

    [Fact]
    public void TheSessionIsToldBeforeAnySurfaceRenders()
    {
        // Order matters when narrowing: the session owns the write decision, so it has to revoke before
        // a page has the chance to render the previous answer.
        var app = Source("src", "NutManager.App", "App.axaml.cs");

        var session = app.IndexOf("remoteManagement?.ApplyAccessMode(profile.AccessMode);", StringComparison.Ordinal);
        var page = app.IndexOf("administration.ApplyAccessMode(profile.AccessMode);", StringComparison.Ordinal);

        Assert.True(session > 0, "The session is no longer told about an access-mode change.");
        Assert.True(session < page, "The session must be updated before the surfaces that read from it.");
    }

    [Fact]
    public void TheSidebarCardFollowsTheSavedAccessMode()
    {
        // The card reads a field of its own rather than the profile, so it kept saying Manage beside a
        // header that already said read-only.
        var shell = Source("src", "NutManager.App", "ViewModels", "MainWindowViewModel.cs");

        var apply = shell.IndexOf("public void ApplyAccessMode", StringComparison.Ordinal);
        Assert.True(apply > 0);

        var body = shell[apply..(apply + 800)];
        Assert.Contains("_accessMode = accessMode;", body, StringComparison.Ordinal);
        Assert.Contains("OnPropertyChanged(nameof(ActiveProfileModeText));", body, StringComparison.Ordinal);
    }

    private static ManagedNutServerRuntimeContext RemoteContext(ManagedNutServerAccessMode access)
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "GANDALF",
            new NutMonitoringProfile("gandalf.example.local", 3493, "NOBREAK"),
            new NutManagementProfile(NutManagementMode.Remote, "gandalf.example.local", "/etc/nut"),
            access);

        return ManagedNutServerRuntimeContext.FromProfiles(
            new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]),
            new ApplicationSettings());
    }

    // ---------------------------------------------------------------- remote diagnostics

    [Fact]
    public void ARemoteProfileNeverReportsTheLocalMachineAsTheServersVerdict()
    {
        // "No local NUT installation found" is true about the station and says nothing about the server
        // being managed. Printed in a card an operator opened to diagnose GANDALF, it invites exactly
        // the wrong reading.
        var source = Source("src", "NutManager.App", "ViewModels", "PageViewModels.cs");

        var property = source.IndexOf("public string LocalInstallationStatusText", StringComparison.Ordinal);
        Assert.True(property > 0);

        var expression = source[property..(property + 420)];
        Assert.Contains("IsLocalManagementProfile", expression, StringComparison.Ordinal);
        Assert.Contains("Diagnostics.LocalTechnicalNotApplicable", expression, StringComparison.Ordinal);

        // The not-applicable state exists in both cultures.
        foreach (var language in new[] { UiLanguagePreference.PtBr, UiLanguagePreference.EnUs })
        {
            Assert.False(string.IsNullOrWhiteSpace(
                new NutManagerLocalizer(language).Get("Diagnostics.LocalTechnicalNotApplicable")));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NutManager.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>
    /// Counts what it was asked, so a test can assert that discarding a confirmation reached nothing
    /// and that a rebind reached the new client rather than the old one.
    /// </summary>
    private sealed class CountingAgentClient : INutManagerAgentClient
    {
        private static readonly DateTimeOffset Observed = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        private readonly NutServiceState _state;

        public CountingAgentClient(NutServiceState state) => _state = state;

        public int Probes { get; private set; }

        public int ControlCalls { get; private set; }

        public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken)
        {
            Probes++;
            return Task.FromResult(NutAgentClientResult<NutAgentHandshake>.Ok(
                new NutAgentHandshake(
                    NutAgentOptions.ProtocolVersion,
                    "1.0.0",
                    "GANDALF",
                    [
                        NutAgentOperation.Handshake,
                        NutAgentOperation.GetStatus,
                        NutAgentOperation.Start,
                        NutAgentOperation.Stop,
                        NutAgentOperation.Restart
                    ],
                    true,
                    null),
                NutAgentResultCode.Success));
        }

        public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken)
        {
            Probes++;
            return Task.FromResult(NutAgentClientResult<NutAgentServiceStatus>.Ok(
                new NutAgentServiceStatus("GANDALF", "Network UPS Tools", "Network UPS Tools", _state, 4242, "nut.exe", true, Observed),
                NutAgentResultCode.Success));
        }

        public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("These tests never ask for hardware.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            Control(NutAgentOperation.Start, operationId);

        public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            Control(NutAgentOperation.Stop, operationId);

        public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            Control(NutAgentOperation.Restart, operationId);

        private Task<NutAgentClientResult<NutAgentOperationResult>> Control(NutAgentOperation operation, Guid operationId)
        {
            ControlCalls++;
            return Task.FromResult(NutAgentClientResult<NutAgentOperationResult>.Ok(
                new NutAgentOperationResult(
                    operationId, operation, NutAgentResultCode.Success, _state, _state,
                    NutAgentRestartPhase.None, null, null, null, TimeSpan.Zero, null),
                NutAgentResultCode.Success));
        }
    }
}
