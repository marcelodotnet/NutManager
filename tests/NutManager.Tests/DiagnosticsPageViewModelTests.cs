using NutManager.App.Services;
using NutManager.App.ViewModels;
using NutManager.Core.Models;
using NutManager.Core.Services;
using Xunit;

namespace NutManager.Tests;

public sealed class DiagnosticsPageViewModelTests
{
    private static readonly ApplicationRuntimeInfo RuntimeInfo = new("10.2.3-test+abcdef0", "v10.2.3-test", ".NET test runtime", "Test OS", "TestArchitecture");

    [Fact]
    public void ShowsDeterministicApplicationInformationAndIgnoresLegacyMockPreference()
    {
        using var viewModel = CreateViewModel(new ApplicationSettings(
            pollingInterval: TimeSpan.FromSeconds(8),
            connectionTimeout: TimeSpan.FromSeconds(3),
            mockMode: true),
            profileContext: CreateProfileContext(NutManagementMode.Local, ManagedNutServerAccessMode.Manage));

        Assert.Equal("NUT Manager", viewModel.ApplicationName);
        // The card shows the readable version; the build metadata is cut for display and kept for the
        // report, where identifying the exact build is the point.
        Assert.Equal("v10.2.3-test", viewModel.ApplicationVersion);
        Assert.Equal("10.2.3-test+abcdef0", viewModel.ApplicationBuildVersion);
        Assert.Equal(".NET test runtime", viewModel.Runtime);
        Assert.Equal("Test OS", viewModel.OperatingSystem);
        Assert.Equal("TestArchitecture", viewModel.Architecture);
        Assert.Equal("Servidor NUT real", viewModel.ModeText);
        Assert.Equal("monitor.example", viewModel.Host);
        Assert.Equal("3494", viewModel.Port);
        Assert.Equal("3 s", viewModel.ConnectionTimeoutText);
        Assert.Equal("8 s", viewModel.PollingIntervalText);
        Assert.Equal("remote-ups", viewModel.PreferredUpsName);
    }

    [Fact]
    public void ShowsLiveModeAndMissingPreferredUpsText()
    {
        using var viewModel = CreateViewModel(new ApplicationSettings(mockMode: false));

        Assert.Equal("Servidor NUT real", viewModel.ModeText);
        Assert.Equal("Não configurado", viewModel.PreferredUpsName);
    }

    [Fact]
    public void MapsConnectionStatesAndDataFreshnessToPortugueseText()
    {
        var coordinator = new TestPollingCoordinator();
        using var viewModel = CreateViewModel(new ApplicationSettings(), coordinator);

        foreach (var (state, expected) in new[]
        {
            (ConnectionState.Disconnected, "Desconectado"),
            (ConnectionState.Connecting, "Conectando"),
            (ConnectionState.Connected, "Conectado"),
            (ConnectionState.Reconnecting, "Reconectando"),
            (ConnectionState.ConnectionFailed, "Falha de conexão")
        })
        {
            coordinator.Publish(new PollingState(null, null, state, DataFreshness.Unavailable, null));
            Assert.Equal(expected, viewModel.ConnectionStateText);
        }

        foreach (var (freshness, expected) in new[]
        {
            (DataFreshness.Unavailable, "Indisponível"),
            (DataFreshness.Fresh, "Atualizado"),
            (DataFreshness.Stale, "Dados desatualizados")
        })
        {
            coordinator.Publish(new PollingState(null, null, ConnectionState.Disconnected, freshness, null));
            Assert.Equal(expected, viewModel.DataFreshnessText);
        }
    }

    [Fact]
    public void ShowsExplicitEmptySnapshotAndNoErrorStates()
    {
        using var viewModel = CreateViewModel(new ApplicationSettings());

        Assert.Equal("Sem snapshot disponível", viewModel.SnapshotStatusText);
        Assert.Equal("Indisponível", viewModel.DataSourceText);
        Assert.Equal("Indisponível", viewModel.LastSuccessfulUpdateText);
        Assert.Equal("Nenhum erro", viewModel.LastErrorText);
        Assert.Equal("Nenhum UPS selecionado", viewModel.SelectedUpsName);
    }

    [Fact]
    public void ReflectsPollingStateAndRetainsSnapshotDataWhileStale()
    {
        var coordinator = new TestPollingCoordinator();
        var snapshot = CreateSnapshot("ups-a", DataSource.Simulated);
        using var viewModel = CreateViewModel(new ApplicationSettings(), coordinator);

        coordinator.Publish(new PollingState("ups-a", snapshot, ConnectionState.Connected, DataFreshness.Fresh, null));

        Assert.Equal("ups-a", viewModel.SelectedUpsName);
        Assert.Equal("UPS de teste", viewModel.SelectedUpsDescription);
        Assert.Equal("Fabricante", viewModel.Manufacturer);
        Assert.Equal("Modelo", viewModel.Model);
        Assert.Equal("Serial", viewModel.SerialNumber);
        Assert.Equal("Snapshot disponível", viewModel.SnapshotStatusText);
        Assert.Equal("Dados simulados", viewModel.DataSourceText);
        Assert.Contains("2026", viewModel.LastSuccessfulUpdateText);

        coordinator.Publish(new PollingState("ups-a", snapshot, ConnectionState.Reconnecting, DataFreshness.Stale, "Falha de atualização."));

        Assert.Equal("Reconectando", viewModel.ConnectionStateText);
        Assert.Equal("Dados desatualizados", viewModel.DataFreshnessText);
        Assert.Equal("UPS de teste", viewModel.SelectedUpsDescription);
        Assert.Equal("Falha de atualização.", viewModel.LastErrorText);
    }

    [Fact]
    public void RecoveryAndUpsChangeUpdateTheDiagnostics()
    {
        var coordinator = new TestPollingCoordinator();
        using var viewModel = CreateViewModel(new ApplicationSettings(), coordinator);
        var first = CreateSnapshot("ups-a", DataSource.Simulated);
        var recovered = CreateSnapshot("ups-b", DataSource.Live);

        coordinator.Publish(new PollingState("ups-a", first, ConnectionState.Reconnecting, DataFreshness.Stale, "Erro anterior"));
        coordinator.Publish(new PollingState("ups-b", recovered, ConnectionState.Connected, DataFreshness.Fresh, null));

        Assert.Equal("ups-b", viewModel.SelectedUpsName);
        Assert.Equal("Servidor NUT", viewModel.DataSourceText);
        Assert.Equal("Conectado", viewModel.ConnectionStateText);
        Assert.Equal("Atualizado", viewModel.DataFreshnessText);
        Assert.Equal("Nenhum erro", viewModel.LastErrorText);
    }

    [Fact]
    public void ReflectsSharedDiscoveryAndSelectedDeviceWithoutStartingOperations()
    {
        var coordinator = new TestPollingCoordinator();
        using var devices = new DevicesPageViewModel();
        using var viewModel = CreateViewModel(new ApplicationSettings(), coordinator, devices);

        devices.Devices =
        [
            new UpsIdentity("ups-a", "Primeiro UPS"),
            new UpsIdentity("ups-b", "Segundo UPS")
        ];
        devices.SelectedDevice = devices.Devices[1];

        Assert.Equal(2, viewModel.DiscoveredUpsCount);
        Assert.Equal("ups-b", viewModel.SelectedUpsName);
        Assert.Equal("Segundo UPS", viewModel.SelectedUpsDescription);
        Assert.Equal(0, coordinator.MonitorCalls);
        Assert.Equal(0, coordinator.RefreshCalls);

        devices.Devices = Array.Empty<UpsIdentity>();
        devices.SelectedDevice = null;
        coordinator.Publish(PollingState.Unavailable);

        Assert.Equal(0, viewModel.DiscoveredUpsCount);
        Assert.Equal("Nenhum UPS selecionado", viewModel.SelectedUpsName);
    }

    [Fact]
    public void UsesUnavailableTextForOptionalIdentityValues()
    {
        var coordinator = new TestPollingCoordinator();
        using var viewModel = CreateViewModel(new ApplicationSettings(), coordinator);
        var snapshot = new UpsSnapshot(new UpsIdentity("ups"), [], new Dictionary<string, UpsVariable>(), DateTimeOffset.UtcNow, DataSource.Live);

        coordinator.Publish(new PollingState("ups", snapshot, ConnectionState.Connected, DataFreshness.Fresh, null));

        Assert.Equal("Indisponível", viewModel.SelectedUpsDescription);
        Assert.Equal("Indisponível", viewModel.Manufacturer);
        Assert.Equal("Indisponível", viewModel.Model);
        Assert.Equal("Indisponível", viewModel.SerialNumber);
    }

    [Fact]
    public async Task LocalInstallationDetectionShowsLoadingSuccessAndManualInspectionStates()
    {
        var detector = new TestInstallationDetector();
        var installation = new NutInstallationInfo(
            true,
            @"C:\NUT",
            @"C:\NUT\etc",
            "2.8.2",
            new Dictionary<string, string> { ["upsc.exe"] = @"C:\NUT\bin\upsc.exe" },
            [new NutConfigurationFileInfo("ups.conf", @"C:\NUT\etc\ups.conf", true, true)],
            @"C:\NUT");
        detector.DetectCompletion = new TaskCompletionSource<NutInstallationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = CreateViewModel(new ApplicationSettings(), installationDetector: detector);

        Assert.Equal("Nenhuma instalação NUT local encontrada", viewModel.LocalInstallationStatusText);
        Assert.True(viewModel.IsLocalManagementProfile);
        Assert.True(viewModel.CanInspectLocalInstallation);
        var refresh = viewModel.RefreshLocalInstallationAsync();
        await detector.DetectStarted.Task;
        Assert.True(viewModel.IsDetectingLocalInstallation);
        detector.DetectCompletion.SetResult(installation);
        await refresh;

        Assert.False(viewModel.IsDetectingLocalInstallation);
        Assert.Equal("Instalação NUT encontrada", viewModel.LocalInstallationStatusText);
        Assert.Equal(@"C:\NUT", viewModel.InstallationDirectoryText);
        Assert.Equal(@"C:\NUT\etc", viewModel.ConfigurationDirectoryText);
        Assert.Equal("2.8.2", viewModel.LocalInstallationVersionText);
        Assert.Contains("upsc.exe", viewModel.ExecutablesText);
        Assert.Contains("ups.conf: Disponível", viewModel.ConfigurationFilesText);

        await viewModel.InspectLocalInstallationDirectoryAsync(@"D:\NUT");
        Assert.Equal(@"D:\NUT", detector.LastInspectedDirectory);
        Assert.Equal(1, detector.ManualInspectionCalls);
    }

    [Fact]
    public async Task LocalInstallationDetectionShowsNotDetectedAndConciseErrors()
    {
        var detector = new TestInstallationDetector
        {
            DetectResult = NutInstallationInfo.NotDetected(),
            InspectionException = new IOException("technical detail")
        };
        using var viewModel = CreateViewModel(new ApplicationSettings(), installationDetector: detector);

        await viewModel.RefreshLocalInstallationAsync();
        Assert.Equal("Nenhuma instalação NUT local encontrada", viewModel.LocalInstallationStatusText);
        Assert.Equal("Indisponível", viewModel.InstallationDirectoryText);

        await viewModel.InspectLocalInstallationDirectoryAsync(@"D:\Denied");
        Assert.True(viewModel.HasLocalInstallationError);
        Assert.Equal("Não foi possível inspecionar a instalação local do NUT.", viewModel.LocalInstallationError);
        Assert.DoesNotContain("technical detail", viewModel.LocalInstallationError);

        await viewModel.RefreshLocalInstallationAsync();
        Assert.Equal(2, detector.DetectCalls);
    }

    [Theory]
    [InlineData(ManagedNutServerAccessMode.Manage)]
    [InlineData(ManagedNutServerAccessMode.ReadOnly)]
    public void LocalManagedProfilesAllowLocalInstallationInspection(ManagedNutServerAccessMode accessMode)
    {
        var detector = new TestInstallationDetector();
        using var viewModel = CreateViewModel(
            new ApplicationSettings(),
            installationDetector: detector,
            profileContext: CreateProfileContext(NutManagementMode.Local, accessMode));

        Assert.True(viewModel.IsLocalManagementProfile);
        Assert.True(viewModel.CanInspectLocalInstallation);
    }

    [Theory]
    [InlineData(ManagedNutServerAccessMode.Manage)]
    [InlineData(ManagedNutServerAccessMode.ReadOnly)]
    public async Task RemoteRuntimeProfilesNeverCallOrApplyLocalInstallation(ManagedNutServerAccessMode accessMode)
    {
        var context = CreateProfileContext(NutManagementMode.Remote, accessMode);
        var detector = new TestInstallationDetector();
        using var viewModel = CreateViewModel(new ApplicationSettings(), installationDetector: detector, profileContext: context);

        await viewModel.RefreshLocalInstallationAsync();
        await viewModel.InspectLocalInstallationDirectoryAsync(@"C:\NUT");

        Assert.Equal("Servidor remoto", viewModel.ManagedProfileName);
        Assert.Equal("monitor.example", viewModel.Host);
        Assert.Equal("3494", viewModel.Port);
        Assert.Equal("remote-ups", viewModel.PreferredUpsName);
        Assert.Equal("Remoto", viewModel.ManagementModeText);
        Assert.Equal(accessMode == ManagedNutServerAccessMode.ReadOnly ? "Somente leitura" : "Permitir gerenciamento", viewModel.ManagementAccessText);
        Assert.False(viewModel.IsLocalManagementProfile);
        Assert.False(viewModel.CanInspectLocalInstallation);
        Assert.Equal(0, detector.DetectCalls);
        Assert.Equal(0, detector.ManualInspectionCalls);
        Assert.Equal("Indisponível", viewModel.InstallationDirectoryText);
        Assert.Contains("não será inspecionada", viewModel.LocalInstallationError);
    }

    private static DiagnosticsPageViewModel CreateViewModel(
        ApplicationSettings settings,
        TestPollingCoordinator? coordinator = null,
        DevicesPageViewModel? devices = null,
        ILocalNutInstallationDetector? installationDetector = null,
        ManagedNutServerRuntimeContext? profileContext = null) =>
        new(settings, RuntimeInfo, coordinator, devices, installationDetector, profileContext);

    private static ManagedNutServerRuntimeContext CreateProfileContext(
        NutManagementMode managementMode,
        ManagedNutServerAccessMode accessMode)
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            managementMode == NutManagementMode.Local ? "Servidor local" : "Servidor remoto",
            new NutMonitoringProfile("monitor.example", 3494, "remote-ups"),
            managementMode == NutManagementMode.Local
                ? new NutManagementProfile(NutManagementMode.Local)
                : new NutManagementProfile(NutManagementMode.Remote, "management.example", "/etc/nut"),
            accessMode);
        return ManagedNutServerRuntimeContext.FromProfiles(
            new ManagedNutServerProfiles(1, profile.Id, [profile]),
            new ApplicationSettings());
    }

    private static UpsSnapshot CreateSnapshot(string name, DataSource source) => new(
        new UpsIdentity(name, "UPS de teste", "Fabricante", "Modelo", "Serial"),
        [],
        new Dictionary<string, UpsVariable>(),
        new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
        source);

    private sealed class TestPollingCoordinator : IUpsPollingCoordinator
    {
        public PollingState State { get; private set; } = PollingState.Unavailable;
        public event Action<PollingState>? StateChanged;
        public int MonitorCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task MonitorAsync(string? upsName, CancellationToken cancellationToken = default)
        {
            MonitorCalls++;
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }

        public void Publish(PollingState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestInstallationDetector : ILocalNutInstallationDetector
    {
        public TaskCompletionSource<NutInstallationInfo> DetectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<NutInstallationInfo>? DetectCompletion { get; set; }
        public NutInstallationInfo DetectResult { get; set; } = NutInstallationInfo.NotDetected();
        public Exception? InspectionException { get; set; }
        public int DetectCalls { get; private set; }
        public int ManualInspectionCalls { get; private set; }
        public string? LastInspectedDirectory { get; private set; }

        public Task<NutInstallationInfo> DetectAsync(CancellationToken cancellationToken)
        {
            DetectCalls++;
            DetectStarted.TrySetResult(DetectResult);
            return DetectCompletion?.Task ?? Task.FromResult(DetectResult);
        }

        public Task<NutInstallationInfo> InspectDirectoryAsync(string installationOrConfigurationDirectory, CancellationToken cancellationToken)
        {
            ManualInspectionCalls++;
            LastInspectedDirectory = installationOrConfigurationDirectory;
            return InspectionException is null
                ? Task.FromResult(DetectResult)
                : Task.FromException<NutInstallationInfo>(InspectionException);
        }
    }
}
