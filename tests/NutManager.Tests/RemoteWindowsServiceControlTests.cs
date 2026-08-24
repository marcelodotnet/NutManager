using System.Reflection;
using System.Windows.Input;
using NutManager.App.ViewModels;
using NutManager.Core.Administration;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Infrastructure.Persistence;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// T35 remote control: the separate control object, its confirmations, and the profile setting that
/// selects the transport.
///
/// Everything runs against a recording fake client. The assertions that matter most are negative —
/// that nothing reached the agent before the operator confirmed, that a restart is one request rather
/// than a stop followed by a start, and that no failure of the agent turns into a second path to the
/// SCM.
/// </summary>
public sealed class RemoteWindowsServiceControlTests
{
    private static readonly DateTimeOffset Observed = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- separation

    [Fact]
    public void TheMonitorStillExposesNoWayToStartStopOrRestart()
    {
        // The T34 boundary, restated now that control exists somewhere. If control had been added to
        // the monitor instead of beside it, this is the test that would have caught it.
        var members = typeof(RemoteWindowsServiceViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .ToArray();

        // StartMonitoring and StopMonitoringAsync are the polling lifetime, not service control, so
        // the check is for members that would act on the service rather than for the words.
        foreach (var forbidden in new[]
        {
            "StartAsync", "StopAsync", "RestartAsync", "StartService", "StopService", "RestartService",
            "StartCommand", "StopCommand", "RestartCommand"
        })
        {
            Assert.DoesNotContain(forbidden, members, StringComparer.Ordinal);
        }

        var commands = typeof(RemoteWindowsServiceViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(["RefreshCommand"], commands);
    }

    // ---------------------------------------------------------------- confirmation

    [Fact]
    public async Task StopSendsNothingUntilItIsConfirmed()
    {
        var (control, client) = await BuildAsync(NutServiceState.Running);

        control.Stop();

        Assert.True(control.IsConfirming);
        Assert.Empty(client.Operations);
    }

    [Fact]
    public async Task CancellingAStopSendsNothingAtAll()
    {
        var (control, client) = await BuildAsync(NutServiceState.Running);

        control.Stop();
        control.CancelConfirmation();

        Assert.False(control.IsConfirming);
        Assert.Empty(client.Operations);
    }

    [Fact]
    public async Task ConfirmingAStopSendsExactlyOneStop()
    {
        var (control, client) = await BuildAsync(NutServiceState.Running);

        control.Stop();
        await control.ConfirmAsync();

        Assert.Equal([NutAgentOperation.Stop], client.Operations);
        Assert.False(control.IsConfirming);
    }

    [Fact]
    public async Task CancellingARestartSendsNothingAtAll()
    {
        var (control, client) = await BuildAsync(NutServiceState.Running);

        control.Restart();
        control.CancelConfirmation();

        Assert.Empty(client.Operations);
    }

    [Fact]
    public async Task ConfirmingARestartSendsOneRestartAndNeverAStopFollowedByAStart()
    {
        // The atomicity belongs to the agent, which holds its mutation gate across both phases. A
        // client-side stop-then-start would leave a window where another request could interleave.
        var (control, client) = await BuildAsync(NutServiceState.Running);

        control.Restart();
        await control.ConfirmAsync();

        Assert.Equal([NutAgentOperation.Restart], client.Operations);
        Assert.DoesNotContain(NutAgentOperation.Stop, client.Operations);
        Assert.DoesNotContain(NutAgentOperation.Start, client.Operations);
    }

    [Fact]
    public async Task TheConfirmationNamesTheHostTheActionAndTheService()
    {
        var (control, _) = await BuildAsync(NutServiceState.Running);

        control.Stop();

        var text = control.ConfirmationText!;
        Assert.Contains("Gandalf.sbra.local", text, StringComparison.Ordinal);
        Assert.Contains("Network UPS Tools", text, StringComparison.Ordinal);
        // The action has to be identifiable, not just "are you sure".
        Assert.Contains(control.Strings.Get("RemoteService.Control.Stop"), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartNeedsNoConfirmationBecauseItIsNotTheDangerousDirection()
    {
        var (control, client) = await BuildAsync(NutServiceState.Stopped);

        await control.StartAsync();

        Assert.False(control.IsConfirming);
        Assert.Equal([NutAgentOperation.Start], client.Operations);
    }

    // ---------------------------------------------------------------- operation identity

    [Fact]
    public async Task OneOperationCarriesOneIdentifier()
    {
        var (control, client) = await BuildAsync(NutServiceState.Running);

        control.Stop();
        await control.ConfirmAsync();

        Assert.Single(client.OperationIds);
        Assert.NotEqual(Guid.Empty, client.OperationIds[0]);
    }

    [Fact]
    public async Task TwoSeparateActionsCarrySeparateIdentifiers()
    {
        // The id is what makes a retry idempotent, so two intentions must never share one.
        var (control, client) = await BuildAsync(NutServiceState.Stopped);

        await control.StartAsync();
        await control.StartAsync();

        Assert.Equal(2, client.OperationIds.Count);
        Assert.NotEqual(client.OperationIds[0], client.OperationIds[1]);
    }

    [Fact]
    public async Task AFailedOperationIsNotRetriedAutomatically()
    {
        var (control, client) = await BuildAsync(NutServiceState.Running, code: NutAgentResultCode.ServiceControlFailed);

        control.Stop();
        await control.ConfirmAsync();

        // Whether to try again is the operator's decision, not the client's.
        Assert.Single(client.Operations);
    }

    // ---------------------------------------------------------------- availability

    [Fact]
    public async Task AnAgentThatDoesNotAdvertiseAnOperationDoesNotOfferIt()
    {
        var (control, _) = await BuildAsync(NutServiceState.Running, capabilities:
            [NutAgentOperation.Handshake, NutAgentOperation.GetStatus, NutAgentOperation.Restart]);

        Assert.False(control.CanStart);
        Assert.False(control.CanStop);
        Assert.True(control.CanRestart);
    }

    [Fact]
    public async Task ControlIsOfferedOnlyWhenTheAgentSaysItIsAvailable()
    {
        // A perfectly healthy service reported by an agent whose audit sink is unusable still cannot
        // be controlled, and the buttons follow the agent rather than the service state.
        var (control, _) = await BuildAsync(NutServiceState.Running, controlAvailable: false);

        Assert.False(control.CanStart);
        Assert.False(control.CanStop);
        Assert.False(control.CanRestart);
    }

    [Fact]
    public async Task AnUnreachableAgentOffersNothing()
    {
        var monitor = new RemoteWindowsServiceViewModel("Gandalf.sbra.local", new RecordingClient(NutAgentClientStatus.AgentUnavailable));
        await using var _ = monitor;
        var control = new RemoteWindowsServiceControlViewModel(monitor, new RecordingClient(NutAgentClientStatus.AgentUnavailable));
        await monitor.RefreshAsync();

        Assert.False(control.CanStart);
        Assert.False(control.CanStop);
        Assert.False(control.CanRestart);
    }

    [Fact]
    public async Task ARunningServiceIsNotOfferedAStart()
    {
        var (control, _) = await BuildAsync(NutServiceState.Running);

        Assert.False(control.CanStart);
        Assert.True(control.CanStop);
    }

    [Fact]
    public async Task AStoppedServiceIsNotOfferedAStop()
    {
        var (control, _) = await BuildAsync(NutServiceState.Stopped);

        Assert.True(control.CanStart);
        Assert.False(control.CanStop);
    }

    [Fact]
    public async Task NothingIsOfferedWhileAConfirmationIsWaiting()
    {
        var (control, _) = await BuildAsync(NutServiceState.Running);

        control.Restart();

        Assert.False(control.CanStart);
        Assert.False(control.CanStop);
        Assert.False(control.CanRestart);
    }

    // ---------------------------------------------------------------- results

    [Fact]
    public async Task ARestartThatLeftTheServiceDownSaysSoRatherThanReportingAGenericFailure()
    {
        // The single most important fact at that moment is that the service is down, not that a
        // restart "failed".
        var client = new RecordingClient(NutServiceState.Running)
        {
            RestartOutcome = new NutAgentOperationResult(
                Guid.NewGuid(), NutAgentOperation.Restart, NutAgentResultCode.ServiceControlFailed,
                NutServiceState.Running, NutServiceState.Stopped, NutAgentRestartPhase.Start,
                NutAgentResultCode.Success, NutAgentResultCode.ServiceControlFailed, null, TimeSpan.Zero, null)
        };

        var monitor = new RemoteWindowsServiceViewModel("Gandalf.sbra.local", client);
        await using var _ = monitor;
        var control = new RemoteWindowsServiceControlViewModel(monitor, client);
        await monitor.RefreshAsync();

        control.Restart();
        await control.ConfirmAsync();

        Assert.Equal(control.Strings.Get("RemoteService.Control.RestartLeftStopped"), control.ResultText);
    }

    [Fact]
    public async Task AnOperationRefusedByTheAgentIsReportedInTheAgentsTerms()
    {
        var (control, _) = await BuildAsync(NutServiceState.Running, code: NutAgentResultCode.Unauthorized);

        control.Stop();
        await control.ConfirmAsync();

        Assert.Equal(control.Strings.Get("RemoteService.Control.Unauthorized"), control.ResultText);
    }

    [Fact]
    public async Task AnOperationThatCouldNotBeRecordedSaysItRanAnyway()
    {
        var (control, _) = await BuildAsync(NutServiceState.Running, code: NutAgentResultCode.CompletedWithAuditFailure);

        control.Stop();
        await control.ConfirmAsync();

        Assert.Equal(control.Strings.Get("RemoteService.Control.CompletedWithAuditFailure"), control.ResultText);
    }

    // ---------------------------------------------------------------- no fallback

    [Fact]
    public void TheApplicationNeverFallsBackToTheDirectScmWhenTheAgentFails()
    {
        var source = Repository.Read(Path.Combine("src", "NutManager.App", "App.axaml.cs"));
        var factory = Repository.Read(Path.Combine("src", "NutManager.App", "Services", "NutAgentClientFactory.cs"));

        // The monitor is built from the agent client and nothing else. A second path would make
        // "control is unavailable" unexplainable: an operator could not tell which route answered.
        Assert.DoesNotContain("WindowsRemoteNutServiceProbe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsRemoteNutServiceProbe", factory, StringComparison.Ordinal);
        Assert.Contains("WindowsNamedPipeNutAgentClient", factory, StringComparison.Ordinal);

        var control = Repository.Read(Path.Combine("src", "NutManager.App", "ViewModels", "RemoteWindowsServiceControlViewModel.cs"));
        foreach (var forbidden in new[] { "IRemoteWindowsNutServiceProbe", "ServiceController", "Process.Start", "sc.exe", "net use" })
        {
            Assert.DoesNotContain(forbidden, control, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheRemoteScmProbeHasNoProductionConsumerLeft()
    {
        // The supersession audit, as a fact rather than a claim. T35 moved the reading to the agent,
        // so nothing in the application composes the remote SCM probe any more. The type itself is
        // kept because the agent still uses its host- and executable-name helpers, which is why this
        // asserts the absence of a consumer rather than the absence of the file.
        var production = Directory
            .EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith("IRemoteWindowsNutServiceProbe.cs", StringComparison.Ordinal))
            .Where(path => !path.EndsWith("WindowsRemoteNutServiceProbe.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("IRemoteWindowsNutServiceProbe", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(production);
    }

    [Fact]
    public void AConfigurationApplyStillNeverRestartsTheService()
    {
        // T14's rule survives the arrival of a remote restart button: nothing in the configuration
        // pipeline may reach the agent.
        foreach (var file in new[]
        {
            Path.Combine("src", "NutManager.Infrastructure", "Configuration", "NutConfigurationFilePipeline.cs"),
            Path.Combine("src", "NutManager.Infrastructure", "Configuration", "RemoteNutConfigurationFilePipeline.cs")
        })
        {
            var source = Repository.Read(file);
            Assert.DoesNotContain("INutManagerAgentClient", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RestartAsync", source, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- profile

    [Fact]
    public void ARemoteProfileWithoutAnAgentSettingTakesTheNamedPipe()
    {
        var management = new NutManagementProfile(
            NutManagementMode.Remote, "gandalf", "/etc/nut", sshUsername: "operator");

        Assert.Equal(NutAgentTransportKind.NamedPipe, management.Agent.Transport);
        Assert.Null(management.Agent.HttpsEndpoint);
    }

    [Fact]
    public void TheAgentTransportIsIndependentOfTheConfigurationTransport()
    {
        // Editing configuration over SMB while controlling the service over a named pipe is an
        // ordinary combination, and one setting must not decide the other.
        var management = new NutManagementProfile(
            NutManagementMode.Remote,
            configurationTransport: RemoteConfigurationTransportKind.Smb,
            smbSharePath: @"\\gandalf\nut",
            agent: new NutAgentProfileSettings(NutAgentTransportKind.NamedPipe));

        Assert.Equal(RemoteConfigurationTransportKind.Smb, management.ConfigurationTransport);
        Assert.Equal(NutAgentTransportKind.NamedPipe, management.Agent.Transport);
    }

    [Theory]
    [InlineData("http://gandalf.sbra.local:5199")]
    [InlineData("ftp://gandalf")]
    [InlineData("file://gandalf/share")]
    [InlineData(@"\\gandalf\pipe")]
    [InlineData("https://user:secret@gandalf")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void OnlyAnAbsoluteHttpsAddressIsAcceptedAsAnAgentEndpoint(string candidate)
    {
        // A plain-text path to a service-control agent is not a degraded option, it is a different
        // product. Credentials in the authority are refused for the same reason.
        Assert.False(NutAgentProfileSettings.IsValidHttpsEndpoint(candidate));
        Assert.Throws<ArgumentException>(() => new NutAgentProfileSettings(NutAgentTransportKind.Https, candidate));
    }

    [Fact]
    public void AValidHttpsEndpointIsKept()
    {
        var settings = new NutAgentProfileSettings(NutAgentTransportKind.Https, "https://gandalf.sbra.local:5199/");

        Assert.Equal(NutAgentTransportKind.Https, settings.Transport);
        Assert.Contains("gandalf.sbra.local", settings.HttpsEndpoint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchingBackToTheNamedPipeDropsTheHttpsEndpoint()
    {
        // A stale address nothing validates any more is worse than no address.
        var settings = new NutAgentProfileSettings(NutAgentTransportKind.NamedPipe, "https://gandalf.sbra.local");

        Assert.Null(settings.HttpsEndpoint);
    }

    [Fact]
    public async Task ALegacyProfileDocumentLoadsAndTakesTheNamedPipe()
    {
        // Schema 5 knew nothing about an agent. It must keep opening, and it must not acquire a
        // transport it never chose.
        var directory = Directory.CreateTempSubdirectory("nutmanager-agent-profile");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "managed-servers.json"), LegacyDocument);

            var loaded = await new JsonManagedNutServerProfileStore(directory.FullName).LoadAsync(default);

            Assert.NotNull(loaded);
            var profile = loaded!.Profiles.Single();
            Assert.Equal(NutManagementMode.Remote, profile.Management.Mode);
            Assert.Equal(NutAgentTransportKind.NamedPipe, profile.Management.Agent.Transport);
            Assert.Null(profile.Management.Agent.HttpsEndpoint);

            // The rest of the document survived untouched.
            Assert.Equal("Gandalf", profile.Name);
            Assert.Equal(RemoteConfigurationTransportKind.SshSftp, profile.Management.ConfigurationTransport);
            Assert.Equal("operator", profile.Management.SshUsername);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private const string LegacyDocument = """
        {
          "schemaVersion": 5,
          "activeProfileId": "11111111-1111-1111-1111-111111111111",
          "profiles": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Gandalf",
              "monitoringHost": "Gandalf.sbra.local",
              "monitoringPort": 3493,
              "managementMode": "Remote",
              "managementHost": "Gandalf.sbra.local",
              "remoteConfigurationDirectory": "/etc/nut",
              "sshPort": 22,
              "sshUsername": "operator",
              "configurationTransport": "SshSftp",
              "sshAuthenticationMode": "Password",
              "accessMode": "Manage"
            }
          ]
        }
        """;

    // ---------------------------------------------------------------- helpers

    private static async Task<(RemoteWindowsServiceControlViewModel Control, RecordingClient Client)> BuildAsync(
        NutServiceState state,
        NutAgentResultCode code = NutAgentResultCode.Success,
        bool controlAvailable = true,
        IReadOnlyList<NutAgentOperation>? capabilities = null)
    {
        var client = new RecordingClient(state)
        {
            Code = code,
            ControlAvailable = controlAvailable,
            Capabilities = capabilities
        };

        var monitor = new RemoteWindowsServiceViewModel("Gandalf.sbra.local", client);
        var control = new RemoteWindowsServiceControlViewModel(monitor, client);
        await monitor.RefreshAsync();
        return (control, client);
    }

    /// <summary>Records what was asked of the agent, which is what most of these tests assert on.</summary>
    private sealed class RecordingClient : INutManagerAgentClient
    {
        private readonly NutServiceState _state;
        private readonly NutAgentClientStatus _transport;

        public RecordingClient(NutServiceState state)
        {
            _state = state;
            _transport = NutAgentClientStatus.Success;
        }

        public RecordingClient(NutAgentClientStatus transport)
        {
            _state = NutServiceState.Unknown;
            _transport = transport;
        }

        public List<NutAgentOperation> Operations { get; } = [];

        public List<Guid> OperationIds { get; } = [];

        public NutAgentResultCode Code { get; init; } = NutAgentResultCode.Success;

        public bool ControlAvailable { get; init; } = true;

        public IReadOnlyList<NutAgentOperation>? Capabilities { get; init; }

        public NutAgentOperationResult? RestartOutcome { get; init; }

        public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken)
        {
            if (_transport != NutAgentClientStatus.Success)
            {
                return Task.FromResult(NutAgentClientResult<NutAgentHandshake>.Failure(_transport));
            }

            var capabilities = Capabilities ?? (ControlAvailable
                ? [NutAgentOperation.Handshake, NutAgentOperation.GetStatus, NutAgentOperation.Start, NutAgentOperation.Stop, NutAgentOperation.Restart]
                : new[] { NutAgentOperation.Handshake, NutAgentOperation.GetStatus });

            return Task.FromResult(NutAgentClientResult<NutAgentHandshake>.Ok(
                new NutAgentHandshake(NutAgentOptions.ProtocolVersion, "1.0.0", "GANDALF", capabilities, ControlAvailable,
                    ControlAvailable ? null : "audit sink unavailable"),
                NutAgentResultCode.Success));
        }

        public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken) =>
            _transport != NutAgentClientStatus.Success
                ? Task.FromResult(NutAgentClientResult<NutAgentServiceStatus>.Failure(_transport))
                : Task.FromResult(NutAgentClientResult<NutAgentServiceStatus>.Ok(
                    new NutAgentServiceStatus("GANDALF", "Network UPS Tools", "Network UPS Tools", _state, 4242, "nut.exe", true, Observed),
                    NutAgentResultCode.Success));

        public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Control never asks for hardware.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            Record(NutAgentOperation.Start, operationId);

        public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            Record(NutAgentOperation.Stop, operationId);

        public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            Record(NutAgentOperation.Restart, operationId);

        private Task<NutAgentClientResult<NutAgentOperationResult>> Record(NutAgentOperation operation, Guid operationId)
        {
            Operations.Add(operation);
            OperationIds.Add(operationId);

            var result = RestartOutcome is { } outcome && operation == NutAgentOperation.Restart
                ? outcome
                : new NutAgentOperationResult(
                    operationId, operation, Code, _state, _state, NutAgentRestartPhase.None, null, null, null, TimeSpan.Zero, null);

            return Task.FromResult(NutAgentClientResult<NutAgentOperationResult>.Ok(result, Code));
        }
    }
}
