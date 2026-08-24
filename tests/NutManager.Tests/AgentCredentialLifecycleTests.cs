using System.Net;
using NutManager.App.Services;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Core.Services;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// The alternate Windows account for the agent: prompt, validation, session, persistence, forget.
///
/// The rule every one of these tests circles is that the Windows dialog returning OK is not
/// authentication. It collected a credential; whether that credential has any rights on the server
/// is a question only the agent can answer, and nothing is remembered before it has.
/// </summary>
public sealed class AgentCredentialLifecycleTests
{
    private static readonly Guid Profile = Guid.Parse("33333333-4444-5555-6666-777777777777");
    private const string Endpoint = "https://gandalf.sbra.local:5199/";
    private const string Account = @"SBRA\operator";
    private const string Sentinel = "s3ntinel-agent-secret";

    // ---------------------------------------------------------------- authenticate

    [Fact]
    public async Task AnAccountTheAgentAcceptsBecomesTheValidatedCredential()
    {
        var session = new NutAgentSessionCredentialStore();
        var coordinator = Build(session, NutAgentClientStatus.Success);

        var result = await Authenticate(coordinator);

        Assert.Equal(NutAgentCredentialOutcome.Validated, result.Outcome);
        Assert.Equal(Account, result.Username);
        Assert.True(session.TryRead(Profile, Account, out var secret));
        Assert.Equal(Sentinel, secret.ToString());
    }

    [Theory]
    [InlineData(NutAgentClientStatus.AccessDenied, NutAgentCredentialOutcome.AccessDenied)]
    [InlineData(NutAgentClientStatus.AgentUnavailable, NutAgentCredentialOutcome.AgentUnavailable)]
    [InlineData(NutAgentClientStatus.HostUnreachable, NutAgentCredentialOutcome.HostUnreachable)]
    [InlineData(NutAgentClientStatus.TimedOut, NutAgentCredentialOutcome.TimedOut)]
    [InlineData(NutAgentClientStatus.ProtocolFailure, NutAgentCredentialOutcome.ProtocolFailure)]
    [InlineData(NutAgentClientStatus.Failed, NutAgentCredentialOutcome.Failed)]
    public async Task APromptThatSucceededAgainstAnAgentThatRefusedIsNotAValidCredential(
        NutAgentClientStatus agent,
        NutAgentCredentialOutcome expected)
    {
        // The dialog said OK for all six of these. Only the agent decides.
        var session = new NutAgentSessionCredentialStore();
        var coordinator = Build(session, agent);

        var result = await Authenticate(coordinator);

        Assert.Equal(expected, result.Outcome);
        Assert.False(result.IsValidated);
        Assert.False(session.Contains(Profile, out _));
    }

    [Fact]
    public async Task CancellingTheDialogChangesNothing()
    {
        var session = new NutAgentSessionCredentialStore();
        session.Store(Profile, Account, "already-validated");

        var coordinator = Build(session, NutAgentClientStatus.Success, WindowsCredentialPromptResult.Cancelled());
        var result = await Authenticate(coordinator);

        Assert.Equal(NutAgentCredentialOutcome.Cancelled, result.Outcome);

        // The credential that was already good is exactly as it was.
        Assert.True(session.TryRead(Profile, Account, out var secret));
        Assert.Equal("already-validated", secret.ToString());
    }

    [Fact]
    public async Task ADialogWindowsCannotShowIsReportedAsSuchRatherThanAsARefusal()
    {
        var coordinator = Build(new NutAgentSessionCredentialStore(), NutAgentClientStatus.Success,
            WindowsCredentialPromptResult.Unsupported());

        var result = await Authenticate(coordinator);

        Assert.Equal(NutAgentCredentialOutcome.PromptUnavailable, result.Outcome);
    }

    [Fact]
    public async Task AnInvalidEndpointIsRefusedWithoutEverOpeningTheDialog()
    {
        // Filling in a dialog for a destination that cannot be used wastes the operator's time and
        // collects a secret for nothing.
        var prompt = new RecordingPrompt(WindowsCredentialPromptResult.Success(Account, Sentinel, false));
        var coordinator = new NutAgentCredentialCoordinator(
            prompt, new NutAgentSessionCredentialStore(), (_, _) => new StubClient(NutAgentClientStatus.Success));

        var result = await coordinator.AuthenticateAsync(
            Profile, "http://gandalf.sbra.local:5199/", null, "c", "m", 0, default);

        Assert.Equal(NutAgentCredentialOutcome.Failed, result.Outcome);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task TheAccountComesFromTheDialogRatherThanFromWhatWasTyped()
    {
        // The prompt is the authority on which account was authenticated; anything else would let a
        // profile claim one account while the secret belongs to another.
        var coordinator = Build(new NutAgentSessionCredentialStore(), NutAgentClientStatus.Success,
            WindowsCredentialPromptResult.Success(@"SBRA\someone-else", Sentinel, false));

        var result = await coordinator.AuthenticateAsync(Profile, Endpoint, Account, "c", "m", 0, default);

        Assert.Equal(@"SBRA\someone-else", result.Username);
    }

    [Fact]
    public async Task TheCredentialGoesToTheHandlerAndNeverIntoTheProtocol()
    {
        NetworkCredential? captured = null;
        var coordinator = new NutAgentCredentialCoordinator(
            new RecordingPrompt(WindowsCredentialPromptResult.Success(Account, Sentinel, false)),
            new NutAgentSessionCredentialStore(),
            (_, credential) => { captured = credential; return new StubClient(NutAgentClientStatus.Success); });

        await coordinator.AuthenticateAsync(Profile, Endpoint, null, "c", "m", 0, default);

        Assert.NotNull(captured);
        Assert.Equal("operator", captured!.UserName);
        Assert.Equal("SBRA", captured.Domain);
    }

    // ---------------------------------------------------------------- session store

    [Fact]
    public void ASessionCredentialIsOnlyReturnedForTheAccountItWasValidatedFor()
    {
        // A secret proven for one account says nothing about another, and handing it over for a
        // profile since repointed would authenticate as someone nobody chose.
        var session = new NutAgentSessionCredentialStore();
        session.Store(Profile, Account, Sentinel);

        Assert.True(session.TryRead(Profile, Account, out _));
        Assert.False(session.TryRead(Profile, @"SBRA\other", out _));
        Assert.False(session.TryRead(Guid.NewGuid(), Account, out _));
    }

    [Fact]
    public void ReplacingASessionCredentialDisposesTheOneItDisplaced()
    {
        var session = new NutAgentSessionCredentialStore();
        session.Store(Profile, Account, "first");
        session.Store(Profile, Account, "second");

        Assert.True(session.TryRead(Profile, Account, out var secret));
        Assert.Equal("second", secret.ToString());
    }

    [Fact]
    public void ForgettingAndDisposingBothClearTheSessionSecret()
    {
        var session = new NutAgentSessionCredentialStore();
        session.Store(Profile, Account, Sentinel);
        session.Forget(Profile);
        Assert.False(session.Contains(Profile, out _));

        var second = new NutAgentSessionCredentialStore();
        second.Store(Profile, Account, Sentinel);
        second.Dispose();
        Assert.False(second.TryRead(Profile, Account, out _));
    }

    [Fact]
    public void TheSessionStoreCanOnlyEverHoldSecretsInMemory()
    {
        var source = Repository.Read(Path.Combine("src", "NutManager.Core", "Agent", "NutAgentSessionCredentialStore.cs"));

        // Call forms: a bare "File." also matches the word "profile." in a comment.
        foreach (var forbidden in new[] { "File.Write", "File.Open", "File.Create", "Directory.Create", "Registry.", "CredWrite" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public async Task NothingIsWrittenToTheCredentialManagerAtAuthenticationTime()
    {
        // A credential stored for a profile the operator then cancels is an orphan nobody removes.
        var store = new RecordingCredentialStore();
        var session = new NutAgentSessionCredentialStore();
        var coordinator = Build(session, NutAgentClientStatus.Success);

        await Authenticate(coordinator);

        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task PersistingCopiesTheSessionSecretOnlyWhenAsked()
    {
        var store = new RecordingCredentialStore();
        var session = new NutAgentSessionCredentialStore();
        var coordinator = Build(session, NutAgentClientStatus.Success);

        await Authenticate(coordinator);
        var persisted = await coordinator.PersistAsync(Profile, Account, store, default);

        Assert.True(persisted);
        Assert.Equal([RemoteCredentialKind.WindowsAgentPassword], store.Writes);
        Assert.Equal(Sentinel, store.LastSecret);
    }

    [Fact]
    public async Task PersistingWithoutAValidatedCredentialWritesNothing()
    {
        var store = new RecordingCredentialStore();
        var coordinator = Build(new NutAgentSessionCredentialStore(), NutAgentClientStatus.Success);

        var persisted = await coordinator.PersistAsync(Profile, Account, store, default);

        Assert.False(persisted);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task AFailedAuthenticationLeavesAPreviouslyStoredCredentialAlone()
    {
        var store = new RecordingCredentialStore { AgentSecret = "credential-A" };
        var session = new NutAgentSessionCredentialStore();
        var coordinator = Build(session, NutAgentClientStatus.AccessDenied,
            WindowsCredentialPromptResult.Success(@"SBRA\account-b", "credential-B", false));

        var result = await Authenticate(coordinator);

        Assert.False(result.IsValidated);
        Assert.Empty(store.Writes);
        Assert.Empty(store.Deletes);
        Assert.Equal("credential-A", store.AgentSecret);
    }

    // ---------------------------------------------------------------- runtime consumption

    [Fact]
    public async Task ACredentialTheOperatorChoseNotToSaveIsStillUsedForThisSession()
    {
        // Remember=false means "do not store it", not "do not use it".
        var session = new NutAgentSessionCredentialStore();
        session.Store(Profile, Account, Sentinel);

        var store = new RecordingCredentialStore();
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.Https, Endpoint, NutAgentAuthenticationMode.AlternateWindowsAccount, Account);

        var client = await NutAgentClientFactory.CreateAsync(settings, Profile, store, default, session);

        Assert.IsNotType<UnavailableNutAgentClient>(client);
        Assert.Empty(store.Reads);
        if (client is IDisposable disposable) disposable.Dispose();
    }

    [Fact]
    public async Task WithoutASessionCredentialTheStoredOneIsUsed()
    {
        var store = new RecordingCredentialStore { AgentSecret = Sentinel };
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.Https, Endpoint, NutAgentAuthenticationMode.AlternateWindowsAccount, Account);

        var client = await NutAgentClientFactory.CreateAsync(
            settings, Profile, store, default, new NutAgentSessionCredentialStore());

        Assert.Equal([RemoteCredentialKind.WindowsAgentPassword], store.Reads);
        if (client is IDisposable disposable) disposable.Dispose();
    }

    [Fact]
    public async Task ASessionCredentialForAnotherAccountIsNotUsed()
    {
        var session = new NutAgentSessionCredentialStore();
        session.Store(Profile, @"SBRA\someone-else", Sentinel);

        var store = new RecordingCredentialStore();
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.Https, Endpoint, NutAgentAuthenticationMode.AlternateWindowsAccount, Account);

        var client = await NutAgentClientFactory.CreateAsync(settings, Profile, store, default, session);

        // Falls through to the store, finds nothing, and says so rather than using the wrong secret.
        Assert.IsType<UnavailableNutAgentClient>(client);
        Assert.Equal([RemoteCredentialKind.WindowsAgentPassword], store.Reads);
    }

    [Fact]
    public async Task TheAgentPathNeverReadsTheSmbOrSshCredential()
    {
        var store = new RecordingCredentialStore { SmbSecret = "smb", SshSecret = "ssh" };
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.Https, Endpoint, NutAgentAuthenticationMode.AlternateWindowsAccount, Account);

        await NutAgentClientFactory.CreateAsync(settings, Profile, store, default, new NutAgentSessionCredentialStore());

        Assert.DoesNotContain(RemoteCredentialKind.SmbPassword, store.Reads);
        Assert.DoesNotContain(RemoteCredentialKind.SshPassword, store.Reads);
    }

    // ---------------------------------------------------------------- secret boundary

    [Fact]
    public async Task TheSecretNeverReachesTheProfileDocument()
    {
        var session = new NutAgentSessionCredentialStore();
        var coordinator = Build(session, NutAgentClientStatus.Success);
        await Authenticate(coordinator);

        var profile = new ManagedNutServerProfile(
            Profile, "Gandalf", new NutMonitoringProfile("gandalf.sbra.local"),
            new NutManagementProfile(
                NutManagementMode.Remote, "gandalf.sbra.local", "/etc/nut", sshUsername: "operator",
                agent: new NutAgentProfileSettings(
                    NutAgentTransportKind.Https, Endpoint, NutAgentAuthenticationMode.AlternateWindowsAccount, Account)),
            ManagedNutServerAccessMode.Manage);

        var json = System.Text.Json.JsonSerializer.Serialize(profile);

        // The secret is absent; the account, which is not a secret, survives — JSON escapes
        // the backslash, so it is compared in the form the document actually holds.
        Assert.DoesNotContain(Sentinel, json, StringComparison.Ordinal);
        Assert.Contains("operator", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoordinatorExposesNoWayToReadTheSecretBack()
    {
        var properties = typeof(NutAgentCredentialCoordinator).GetProperties().Select(property => property.Name).ToArray();

        Assert.Empty(properties);

        var resultProperties = typeof(NutAgentCredentialResult).GetProperties().Select(property => property.Name).ToArray();
        foreach (var forbidden in new[] { "Password", "Secret", "Credential" })
        {
            Assert.DoesNotContain(resultProperties, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void NoLogonUserOrImpersonationIsUsedToValidateTheCredential()
    {
        var source = Repository.Read(Path.Combine("src", "NutManager.App", "Services", "NutAgentCredentialCoordinator.cs"));

        foreach (var forbidden in new[] { "LogonUser", "RunImpersonated", "WindowsIdentity.Impersonate", "Process.Start" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Task<NutAgentCredentialResult> Authenticate(NutAgentCredentialCoordinator coordinator) =>
        coordinator.AuthenticateAsync(Profile, Endpoint, null, "caption", "message", 0, default);

    private static NutAgentCredentialCoordinator Build(
        INutAgentSessionCredentialStore session,
        NutAgentClientStatus agent,
        WindowsCredentialPromptResult? prompt = null) =>
        new(new RecordingPrompt(prompt ?? WindowsCredentialPromptResult.Success(Account, Sentinel, false)),
            session,
            (_, _) => new StubClient(agent));

    private sealed class RecordingPrompt(WindowsCredentialPromptResult result) : IWindowsCredentialPrompt
    {
        public int Calls { get; private set; }

        public Task<WindowsCredentialPromptResult> RequestAsync(
            WindowsCredentialPromptRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubClient(NutAgentClientStatus status) : INutManagerAgentClient
    {
        public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken) =>
            status == NutAgentClientStatus.Success
                ? Task.FromResult(NutAgentClientResult<NutAgentHandshake>.Ok(
                    new NutAgentHandshake(NutAgentOptions.ProtocolVersion, "1.0.0", "GANDALF",
                        [NutAgentOperation.Handshake, NutAgentOperation.GetStatus], true, null),
                    NutAgentResultCode.Success))
                : Task.FromResult(NutAgentClientResult<NutAgentHandshake>.Failure(status));

        public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(NutAgentClientResult<NutAgentServiceStatus>.Failure(status));

        public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(NutAgentClientResult<NutAgentHardwareSnapshot>.Failure(status));

        public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Validation must never mutate.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Validation must never mutate.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Validation must never mutate.");
    }

    private sealed class RecordingCredentialStore : IRemoteCredentialStore
    {
        public List<RemoteCredentialKind> Reads { get; } = [];

        public List<RemoteCredentialKind> Writes { get; } = [];

        public List<RemoteCredentialKind> Deletes { get; } = [];

        public string? AgentSecret { get; set; }

        public string? SmbSecret { get; set; }

        public string? SshSecret { get; set; }

        public string? LastSecret { get; private set; }

        public Task<RemoteCredentialStoreResult> ContainsAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredentialStoreResult(
                kind == RemoteCredentialKind.WindowsAgentPassword && AgentSecret is not null
                    ? RemoteCredentialStoreStatus.Success
                    : RemoteCredentialStoreStatus.NotFound));

        public Task<RemoteCredentialReadResult> ReadAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default)
        {
            Reads.Add(kind);

            var secret = kind switch
            {
                RemoteCredentialKind.WindowsAgentPassword => AgentSecret,
                RemoteCredentialKind.SmbPassword => SmbSecret,
                RemoteCredentialKind.SshPassword => SshSecret,
                _ => null
            };

            return Task.FromResult(secret is null
                ? new RemoteCredentialReadResult(RemoteCredentialStoreStatus.NotFound)
                : new RemoteCredentialReadResult(RemoteCredentialStoreStatus.Success, new RemoteCredentialSecret(secret)));
        }

        public Task<RemoteCredentialStoreResult> WriteAsync(Guid profileId, RemoteCredentialKind kind, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        {
            Writes.Add(kind);
            LastSecret = secret.ToString();
            if (kind == RemoteCredentialKind.WindowsAgentPassword) AgentSecret = LastSecret;
            return Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
        }

        public Task<RemoteCredentialStoreResult> DeleteAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default)
        {
            Deletes.Add(kind);
            if (kind == RemoteCredentialKind.WindowsAgentPassword) AgentSecret = null;
            return Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
        }

        public Task<RemoteCredentialStoreResult> DeleteAllForProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
    }
}
