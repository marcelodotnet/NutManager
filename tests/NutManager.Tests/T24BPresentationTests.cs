using NutManager.App.Localization;
using NutManager.App.Services;
using NutManager.App.ViewModels;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Core.Status;
using NutManager.Infrastructure.Mock;
using Xunit;

namespace NutManager.Tests;

public sealed class T24BPresentationTests
{
    [Fact]
    public void AdministrationDefaultsToConfigurationAndKeepsFourFocusedSections()
    {
        var viewModel = new AdministrationPageViewModel();

        Assert.Equal(4, viewModel.AdministrationSections.Count);
        Assert.Equal(AdministrationSection.NutConfiguration, viewModel.SelectedAdministrationSection.Section);
        Assert.True(viewModel.IsNutConfigurationSectionSelected);
        Assert.Single(viewModel.AdministrationSections, section => section.Section == AdministrationSection.WindowsService);
        Assert.Single(viewModel.AdministrationSections, section => section.Section == AdministrationSection.DevicesAndDrivers);
        Assert.Single(viewModel.AdministrationSections, section => section.Section == AdministrationSection.RemoteAccess);
    }

    [Fact]
    public void AdministrationSelectionProjectsExactlyOneSection()
    {
        var viewModel = new AdministrationPageViewModel();

        viewModel.SelectedAdministrationSection = viewModel.AdministrationSections.Single(item => item.Section == AdministrationSection.WindowsService);

        Assert.False(viewModel.IsNutConfigurationSectionSelected);
        Assert.True(viewModel.IsWindowsServiceSectionSelected);
        Assert.False(viewModel.IsDevicesDriversSectionSelected);
        Assert.False(viewModel.IsRemoteAccessSectionSelected);
    }

    [Theory]
    [InlineData(false, AdministrationSection.WindowsService, true)]
    [InlineData(false, AdministrationSection.DevicesAndDrivers, true)]
    [InlineData(false, AdministrationSection.RemoteAccess, false)]
    // The Windows service is applicable on a remote profile since T35: the agent reaches it. Devices
    // and drivers joined it in T38, because the agent now enumerates the server's serial devices
    // read-only. Remote access is unchanged — it has nothing to configure locally.
    [InlineData(true, AdministrationSection.WindowsService, true)]
    [InlineData(true, AdministrationSection.DevicesAndDrivers, true)]
    [InlineData(true, AdministrationSection.RemoteAccess, true)]
    public void AdministrationApplicabilityIsBoundToLocalOrRemoteContext(bool remote, AdministrationSection section, bool expected)
    {
        var sections = AdministrationPresentation.CreateSections(new NutManagerLocalizer(UiLanguagePreference.PtBr), remote, canManage: true);

        Assert.Equal(expected, sections.Single(item => item.Section == section).IsApplicable);
    }

    [Fact]
    public void TheRemoteWindowsServiceSectionIsNotDescribedAsLocalOnly()
    {
        // Observed on a working installation: the agent was connected over HTTPS and reporting the
        // remote service's state and pid, with start, stop and restart offered, while the header
        // said the section was available only for local management. The text predates the agent.
        foreach (var language in new[] { UiLanguagePreference.PtBr, UiLanguagePreference.EnUs })
        {
            var strings = new NutManagerLocalizer(language);
            var remote = AdministrationPresentation.CreateSections(strings, isRemote: true, canManage: true)
                .Single(item => item.Section == AdministrationSection.WindowsService);

            Assert.NotEqual(strings.Get("Administration.Availability.LocalOnly"), remote.AvailabilityText);
            Assert.Equal(strings.Get("Administration.Availability.ViaAgent"), remote.AvailabilityText);
        }
    }

    [Fact]
    public void DevicesAndDriversIsReachedThroughTheAgentOnARemoteProfile()
    {
        // Until T38 the agent controlled a service and nothing else, and this section correctly said
        // it was local-only. The agent now also enumerates the server's serial devices, read-only, so
        // the section is reachable and is described the way the service section is: through the agent.
        // What stayed local is the active driver diagnostics, and the section itself says so.
        foreach (var language in new[] { UiLanguagePreference.PtBr, UiLanguagePreference.EnUs })
        {
            var strings = new NutManagerLocalizer(language);
            var drivers = AdministrationPresentation.CreateSections(strings, isRemote: true, canManage: true)
                .Single(item => item.Section == AdministrationSection.DevicesAndDrivers);

            Assert.True(drivers.IsApplicable);
            Assert.Equal(strings.Get("Administration.Availability.ViaAgent"), drivers.AvailabilityText);
            Assert.NotEqual(strings.Get("Administration.Availability.LocalOnly"), drivers.AvailabilityText);
        }
    }

    [Fact]
    public void DevicesAndDriversIsPlainlyAvailableOnALocalProfile()
    {
        // No agent is involved locally, so naming one here would be wrong in the other direction.
        var strings = new NutManagerLocalizer(UiLanguagePreference.PtBr);
        var drivers = AdministrationPresentation.CreateSections(strings, isRemote: false, canManage: true)
            .Single(item => item.Section == AdministrationSection.DevicesAndDrivers);

        Assert.True(drivers.IsApplicable);
        Assert.Equal(strings.Get("Administration.Availability.Available"), drivers.AvailabilityText);
    }

    [Fact]
    public void TheLocalProfileStillReportsTheServiceAsPlainlyAvailable()
    {
        var strings = new NutManagerLocalizer(UiLanguagePreference.PtBr);
        var local = AdministrationPresentation.CreateSections(strings, isRemote: false, canManage: true)
            .Single(item => item.Section == AdministrationSection.WindowsService);

        // No agent is involved locally, so naming one would be wrong in the other direction.
        Assert.True(local.IsApplicable);
        Assert.Equal(strings.Get("Administration.Availability.Available"), local.AvailabilityText);
    }

    [Fact]
    public void AdministrationOrdinaryPropertiesDoNotAcquirePasswordOrPassphraseValues()
    {
        var names = typeof(AdministrationPageViewModel).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain("Password", names);
        Assert.DoesNotContain("Passphrase", names);
        Assert.DoesNotContain("Secret", names);
    }

    [Fact]
    public void EnglishOverviewUsesLocalizedMetricLabelsAndExplicitMissingValues()
    {
        var viewModel = new OverviewPageViewModel(UiLanguagePreference.EnUs);

        Assert.Equal("Overview", viewModel.Title);
        Assert.Contains(viewModel.MetricCards, metric => metric.Title == "Battery charge" && metric.DisplayValue == "Unavailable");
        Assert.Contains(viewModel.MetricCards, metric => metric.Title == "Runtime" && metric.DisplayValue == "Unavailable");
    }

    [Fact]
    public void DevicesEmptyStateDistinguishesNoDevicesFromUnavailableDetails()
    {
        var viewModel = new DevicesPageViewModel(UiLanguagePreference.EnUs);

        Assert.True(viewModel.HasNoDevices);
        Assert.Equal("No UPS was found on the connected server.", viewModel.EmptyStateText);
        Assert.Equal("Unavailable", viewModel.SelectedDeviceSerialNumber);
    }

    [Fact]
    public void DiagnosticsExposeRequiredGroupsAndDeterministicSanitizedCopy()
    {
        var polling = new TestPollingCoordinator();
        polling.Publish(new PollingState(null, null, ConnectionState.ConnectionFailed, DataFreshness.Unavailable, "password=fictional-secret"));
        using var viewModel = new DiagnosticsPageViewModel(
            new ApplicationSettings(),
            new ApplicationRuntimeInfo("1.2.3", ".NET", "Windows", "x64"),
            polling,
            language: UiLanguagePreference.EnUs);

        Assert.Equal(6, viewModel.DiagnosticGroups.Count);
        var first = viewModel.CreateDiagnosticReport();
        var second = viewModel.CreateDiagnosticReport();
        Assert.Equal(first, second);
        Assert.DoesNotContain("fictional-secret", first, StringComparison.Ordinal);
        Assert.DoesNotContain("password=", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Error state: Present (details redacted)", first, StringComparison.Ordinal);

        viewModel.ReportDiagnosticCopyResult(succeeded: true);
        Assert.True(viewModel.HasDiagnosticCopyStatusMessage);
        Assert.Equal("Diagnostics copied.", viewModel.DiagnosticCopyStatusMessage);
    }

    [Fact]
    public void T24BLocalizationKeysHaveExactParityAndResolveInBothCultures()
    {
        var pt = NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.PtBr);
        var en = NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.EnUs);

        Assert.Equal(pt.Order(), en.Order());
        Assert.All(pt.Where(key => key.StartsWith("Administration.", StringComparison.Ordinal) ||
                                  key.StartsWith("Overview.", StringComparison.Ordinal) ||
                                  key.StartsWith("Devices.", StringComparison.Ordinal) ||
                                  key.StartsWith("Diagnostics.", StringComparison.Ordinal) ||
                                  key.StartsWith("Remote.", StringComparison.Ordinal)),
            key =>
            {
                Assert.NotEqual(key, new NutManagerLocalizer(UiLanguagePreference.PtBr).Get(key));
                Assert.NotEqual(key, new NutManagerLocalizer(UiLanguagePreference.EnUs).Get(key));
            });
    }

    [Fact]
    public void RemoteAdministrationPresentationUsesSelectedCultureWithoutStoringSecrets()
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "Remote",
            new NutMonitoringProfile("monitor.example"),
            new NutManagementProfile(NutManagementMode.Remote, "management.example", "/etc/nut", sshUsername: "nutadmin"),
            ManagedNutServerAccessMode.Manage);
        var viewModel = new RemoteManagementSessionViewModel(
            profile,
            new UnusedRemoteTransport(),
            language: UiLanguagePreference.EnUs);

        Assert.Equal("Not connected", viewModel.ConnectionStateText);
        Assert.Equal("Password", viewModel.SshAuthenticationModeText);
        Assert.Equal("Validate a remote directory to enable reading.", viewModel.ReadCapabilityText);
        Assert.DoesNotContain(viewModel.GetType().GetProperties(), property =>
            property.Name.Contains("Password", StringComparison.Ordinal) && property.PropertyType == typeof(string));
    }

    private sealed class TestPollingCoordinator : IUpsPollingCoordinator
    {
        public PollingState State { get; private set; } = PollingState.Unavailable;
        public event Action<PollingState>? StateChanged;
        public Task MonitorAsync(string? upsName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Publish(PollingState state) { State = state; StateChanged?.Invoke(state); }
        public void Dispose() { }
    }

    private sealed class UnusedRemoteTransport : IRemoteNutConfigurationTransport
    {
        public Task<RemoteNutConnectionResult> ConnectAsync(
            RemoteNutConfigurationConnectionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This presentation test does not connect.");
    }
}
