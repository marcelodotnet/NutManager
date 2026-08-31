using System.Net;
using NutManager.App.Services;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Infrastructure.Agent;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// The optional HTTPS transport.
///
/// None of this opens a listener or binds a port. What it pins down is everything that decides
/// whether a listener may open at all — the configuration rules, the routing, the failure mapping and
/// the credential boundary — because those are the decisions that would let a privileged agent end up
/// reachable in a way nobody intended.
/// </summary>
public sealed class NutAgentHttpsTests
{
    private static readonly Guid Profile = Guid.Parse("22222222-3333-4444-5555-666666666666");

    // ---------------------------------------------------------------- routing

    [Theory]
    [InlineData("POST", "/v1/agent", true)]
    [InlineData("POST", "/v1/agent/", true)]
    [InlineData("GET", "/v1/agent", false)]
    [InlineData("HEAD", "/v1/agent", false)]
    [InlineData("PUT", "/v1/agent", false)]
    [InlineData("POST", "/v1/agent/start", false)]
    [InlineData("POST", "/health", false)]
    [InlineData("POST", "/", false)]
    public void OnlyOneRouteAndOneMethodAreAnswered(string method, string path, bool expected)
    {
        // No GET health endpoint on purpose: an unauthenticated probe on a privileged agent is a way
        // to enumerate servers, and a path that names an operation is a path that can be guessed.
        Assert.Equal(expected, NutAgentHttpsProtocol.IsAgentRoute(method, path));
    }

    [Fact]
    public void TheHttpsTransportCarriesTheSameEnvelopeAsThePipe()
    {
        // One parser for both transports. A second message shape is a second chance to get it wrong.
        Assert.Equal(NutAgentFraming.MaxRequestBytes, NutAgentHttpsProtocol.MaxRequestBytes);
        Assert.Equal(NutAgentFraming.MaxResponseBytes, NutAgentHttpsProtocol.MaxResponseBytes);
    }

    // ---------------------------------------------------------------- client endpoint

    [Fact]
    public void TheClientAlwaysTargetsTheAgentRouteRatherThanWhateverTheProfileNamed()
    {
        // The client type is Windows-typed, so the repository guard for platform tests applies.
        if (!OperatingSystem.IsWindows()) return;

        // Only the origin is taken from configuration. A profile cannot aim the client at another
        // endpoint on the same host by writing a path into the setting.
        var endpoint = WindowsHttpsNutAgentClient.BuildEndpoint("https://gandalf.sbra.local:5199/somewhere/else");

        Assert.Equal("https://gandalf.sbra.local:5199/v1/agent", endpoint.ToString());
    }

    [Theory]
    [InlineData("http://gandalf.sbra.local:5199/")]
    [InlineData("ftp://gandalf")]
    [InlineData("gandalf.sbra.local")]
    public void TheClientRefusesAnythingThatIsNotHttps(string endpoint)
    {
        // The client type is Windows-typed, so the repository guard for platform tests applies.
        if (!OperatingSystem.IsWindows()) return;

        // Written without a lambda: a platform guard does not follow the call into one, and the
        // client type is Windows-typed.
        try
        {
            WindowsHttpsNutAgentClient.BuildEndpoint(endpoint);
            Assert.Fail("The endpoint should have been refused.");
        }
        catch (ArgumentException)
        {
        }
    }

    // ---------------------------------------------------------------- client failure mapping

    [Fact]
    public void ARefusedAccountIsAccessDeniedAndNeverAnOutage()
    {
        // The client type is Windows-typed, so the repository guard for platform tests applies.
        if (!OperatingSystem.IsWindows()) return;

        var status = WindowsHttpsNutAgentClient.MapFailure(
            new HttpRequestException("forbidden", new System.ComponentModel.Win32Exception(5)), out var code);

        Assert.Equal(NutAgentClientStatus.AccessDenied, status);
        Assert.Equal(5, code);
    }

    [Fact]
    public void ACertificateProblemIsItsOwnAnswerRatherThanAnUnreachableHost()
    {
        // The client type is Windows-typed, so the repository guard for platform tests applies.
        if (!OperatingSystem.IsWindows()) return;

        // Reporting a TLS failure as a network failure sends an operator to look at the firewall
        // for a certificate that expired.
        var status = WindowsHttpsNutAgentClient.MapFailure(
            new HttpRequestException("tls", new System.Security.Authentication.AuthenticationException("untrusted")), out _);

        Assert.Equal(NutAgentClientStatus.ProtocolFailure, status);
    }

    [Fact]
    public void ARefusedConnectionIsAnUnreachableHostAndNotAnAuthorizationVerdict()
    {
        // The client type is Windows-typed, so the repository guard for platform tests applies.
        if (!OperatingSystem.IsWindows()) return;

        // SocketException derives from Win32Exception, so the order of the mapping matters: a
        // refused TCP connection must not be reported as the caller being unauthorized.
        var status = WindowsHttpsNutAgentClient.MapFailure(
            new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)), out _);

        Assert.Equal(NutAgentClientStatus.HostUnreachable, status);
    }

    [Fact]
    public void NoHttpsFailureIsEverReportedAsANutOutage()
    {
        // The client type is Windows-typed, so the repository guard for platform tests applies.
        if (!OperatingSystem.IsWindows()) return;

        foreach (var exception in new Exception[]
        {
            new HttpRequestException("x"),
            new System.Security.Authentication.AuthenticationException("x"),
            new TimeoutException("x")
        })
        {
            var status = WindowsHttpsNutAgentClient.MapFailure(exception, out _);
            Assert.NotEqual(NutAgentClientStatus.Success, status);
        }
    }

    // ---------------------------------------------------------------- server configuration

    [Fact]
    public void HttpsIsOffUnlessADeploymentTurnedItOn()
    {
        // The default an installation gets by doing nothing: a named pipe and no open port.
        var options = AgentOptions.Disabled();

        Assert.False(AgentOptions.Validate(options, out var failure));
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("http://gandalf.sbra.local:5199/", "AABBCC", "must use https")]
    [InlineData("https://gandalf.sbra.local:5199", "AABBCC", "forward slash")]
    [InlineData("https://*:5199/", "AABBCC", "wildcard")]
    [InlineData("https://+:5199/", "AABBCC", "wildcard")]
    [InlineData("https://gandalf.sbra.local:5199/", "", "thumbprint")]
    [InlineData("https://gandalf.sbra.local:5199/", "not-hex-zz", "hexadecimal")]
    [InlineData("", "AABBCC", "prefix")]
    public void AnUnusableHttpsConfigurationIsRefusedRatherThanCorrected(string prefix, string thumbprint, string expected)
    {
        // Every one of these would otherwise become a listener that is open but not what the
        // administrator wrote down. A plain-text prefix is the worst of them and is refused first.
        var options = AgentOptions.Enabled(prefix, thumbprint);

        Assert.False(AgentOptions.Validate(options, out var failure));
        Assert.NotNull(failure);
        Assert.Contains(expected, failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWellFormedHttpsConfigurationIsAccepted()
    {
        var options = AgentOptions.Enabled(
            "https://gandalf.sbra.local:5199/", "A909502DD82AE41433E6F83886B00D4277A32A7B");

        Assert.True(AgentOptions.Validate(options, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void TheAgentConfigurationHasNowhereToPutASecret()
    {
        // The certificate is named, never carried. There is no password, no PFX and no client
        // credential in this shape, which is what stops one being added by habit.
        var properties = AgentOptions.OptionsType
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        // Exhaustive on purpose. The list grows only when someone deliberately adds a member and
        // updates this line, which is the moment a secret-bearing property would have to be argued
        // for rather than slipped in by habit.
        Assert.Equal(["NamedPipeEnabled", "HttpsEnabled", "HttpsPrefix", "CertificateThumbprint"], properties);
    }

    // ---------------------------------------------------------------- credential boundary

    [Fact]
    public void TheAgentCredentialIsItsOwnStoredTarget()
    {
        // Same server and same user name do not make the secrets equivalent: one reads configuration
        // files, the other controls a service.
        Assert.NotEqual(RemoteCredentialKind.SmbPassword, RemoteCredentialKind.WindowsAgentPassword);
        Assert.NotEqual(RemoteCredentialKind.SshPassword, RemoteCredentialKind.WindowsAgentPassword);
    }

    [Fact]
    public async Task TheAlternateAccountIsNeverSubstitutedWithTheSmbCredential()
    {
        // The store holds an SMB secret and nothing for the agent. The correct outcome is a refusal
        // that says so, not a connection made with the wrong identity.
        var store = new RecordingCredentialStore { SmbSecret = "smb-secret" };
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.Https, "https://gandalf.sbra.local:5199/",
            NutAgentAuthenticationMode.AlternateWindowsAccount, @"SBRA\operator");

        var client = await NutAgentClientFactory.CreateAsync(settings, Profile, store, default);
        var handshake = await client.HandshakeAsync("gandalf.sbra.local", default);

        Assert.IsType<UnavailableNutAgentClient>(client);
        Assert.Equal(NutAgentClientStatus.Failed, handshake.Status);
        Assert.DoesNotContain(RemoteCredentialKind.SmbPassword, store.Reads);
    }

    [Fact]
    public async Task TheAlternateAccountReadsOnlyTheAgentCredential()
    {
        var store = new RecordingCredentialStore { AgentSecret = "agent-secret" };
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.Https, "https://gandalf.sbra.local:5199/",
            NutAgentAuthenticationMode.AlternateWindowsAccount, @"SBRA\operator");

        var client = await NutAgentClientFactory.CreateAsync(settings, Profile, store, default);

        Assert.Equal([RemoteCredentialKind.WindowsAgentPassword], store.Reads);
        if (client is IDisposable disposable) disposable.Dispose();
    }

    [Fact]
    public async Task TheNamedPipeProfileNeverReachesForACredentialAtAll()
    {
        var store = new RecordingCredentialStore();

        var client = await NutAgentClientFactory.CreateAsync(
            NutAgentProfileSettings.NamedPipeDefault, Profile, store, default);

        Assert.IsType<WindowsNamedPipeNutAgentClient>(client);
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task AProfileThatSelectsHttpsNeverSilentlyGetsANamedPipe()
    {
        // The rule that forbids falling back to the SCM forbids falling back between transports:
        // an operator who cannot tell which one answered cannot diagnose either.
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.Https, "https://gandalf.sbra.local:5199/",
            NutAgentAuthenticationMode.AlternateWindowsAccount, @"SBRA\operator");

        var client = await NutAgentClientFactory.CreateAsync(settings, Profile, new RecordingCredentialStore(), default);

        Assert.IsNotType<WindowsNamedPipeNutAgentClient>(client);
    }

    [Theory]
    [InlineData(@"SBRA\operator", "operator", "SBRA")]
    [InlineData("operator", "operator", null)]
    public void AnAccountNameIsSplitTheWayWindowsWritesIt(string account, string user, string? domain)
    {
        var (parsedUser, parsedDomain) = NutAgentClientFactory.SplitAccount(account);

        Assert.Equal(user, parsedUser);
        Assert.Equal(domain, parsedDomain);
    }

    // ---------------------------------------------------------------- profile

    [Fact]
    public void TheAlternateAccountStoresANameAndNeverAPassword()
    {
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.Https, "https://gandalf.sbra.local:5199/",
            NutAgentAuthenticationMode.AlternateWindowsAccount, @"SBRA\operator");

        var properties = typeof(NutAgentProfileSettings).GetProperties().Select(property => property.Name).ToArray();

        Assert.Equal(@"SBRA\operator", settings.Username);
        foreach (var forbidden in new[] { "Password", "Secret", "Credential", "Token" })
        {
            Assert.DoesNotContain(properties, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheNamedPipeNormalisesAwayAnAlternateAccountItCannotHonour()
    {
        // Over the pipe the caller is whoever Windows already authenticated. Keeping the setting
        // would be a promise the transport cannot keep.
        var settings = new NutAgentProfileSettings(
            NutAgentTransportKind.NamedPipe, null, NutAgentAuthenticationMode.AlternateWindowsAccount, @"SBRA\operator");

        Assert.Equal(NutAgentAuthenticationMode.CurrentWindowsIdentity, settings.Authentication);
        Assert.Null(settings.Username);
    }

    // ---------------------------------------------------------------- source boundaries

    [Fact]
    public void TheHttpsTransportNeverBypassesCertificateValidation()
    {
        foreach (var file in new[]
        {
            Path.Combine("src", "NutManager.Infrastructure", "Agent", "WindowsHttpsNutAgentClient.cs"),
            Path.Combine("src", "NutManager.Agent", "NutAgentHttpsServer.cs")
        })
        {
            var source = Repository.Read(file);
            foreach (var forbidden in new[]
            {
                "DangerousAcceptAnyServerCertificateValidator", "ServerCertificateCustomValidationCallback",
                "CheckCertificateRevocationList = false", "http://", "LogonUser(", "RunImpersonated(",
                // The endpoint opt-out forms. The HttpSys option of the same name is required
                // to be present and false, so it is asserted separately rather than banned here.
                ".AllowAnonymous()", "[AllowAnonymous]", "AuthenticationSchemes.Basic"
            })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ProductionNoLongerUsesHttpListenerAnywhere()
    {
        // The migration to ASP.NET Core HTTP.sys is asserted rather than remembered: HttpListener is
        // the type Microsoft marks as not recommended for new development, and it must not come back
        // through a stray using or a second transport.
        var production = Directory
            .EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("HttpListener", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(production);
    }

    [Fact]
    public void TheHttpsServerIsHostedOnHttpSysAndNeverOnKestrel()
    {
        var source = Repository.Read(Path.Combine("src", "NutManager.Agent", "NutAgentHttpsServer.cs"));

        Assert.Contains("UseHttpSys", source, StringComparison.Ordinal);

        // Kestrel would mean TLS configured inside this process, with a certificate and its password
        // somewhere in reach. HTTP.sys keeps the binding with Windows, where the administrator put it.
        foreach (var forbidden in new[] { "UseKestrel", "ConfigureKestrel", "UseHttps(", "ListenAnyIP", "pfx" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheHttpsServerTurnsOffTheTwoDefaultsThatWouldAcceptAnonymousCallers()
    {
        var source = Repository.Read(Path.Combine("src", "NutManager.Agent", "NutAgentHttpsServer.cs"));

        // HttpSys defaults are Schemes = None and AllowAnonymous = true. Both are set explicitly,
        // because a default must never be what decides whether an agent authenticates.
        Assert.Contains("Authentication.Schemes = AuthenticationSchemes.Negotiate", source, StringComparison.Ordinal);
        Assert.Contains("Authentication.AllowAnonymous = false", source, StringComparison.Ordinal);
        Assert.Contains("MaxRequestBodySize", source, StringComparison.Ordinal);

        // The endpoint must not opt itself back out of authentication.
        Assert.DoesNotContain(".AllowAnonymous()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[AllowAnonymous]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHttpsServerAuthenticatesWithNegotiateAndAuthorizesByGroup()
    {
        var source = Repository.Read(Path.Combine("src", "NutManager.Agent", "NutAgentHttpsServer.cs"));

        // Anonymous is HttpListener's default, so the presence of this assignment is the thing that
        // stops an unauthenticated caller ever reaching the dispatcher.
        Assert.Contains("AuthenticationSchemes.Negotiate", source, StringComparison.Ordinal);
        Assert.Contains("IsInRole", source, StringComparison.Ordinal);
        Assert.Contains("NutAgentRequestDispatcher", source, StringComparison.Ordinal);

        // The control rules stay in the application service; the transport must not grow its own.
        foreach (var forbidden in new[] { "ServiceController", "StartAsync(target", "Process.Start" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoAgentPasswordEverTravelsInTheProtocol()
    {
        var contract = Repository.Read(Path.Combine("src", "NutManager.Core", "Agent", "NutAgentWireContract.cs"));

        foreach (var forbidden in new[] { "Password", "Secret", "NetworkCredential", "AccessToken" })
        {
            Assert.DoesNotContain(forbidden, contract, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Reaches the agent's internal options type by reflection, so the configuration rules can be
    /// tested without making the agent's server-side settings a public API.
    /// </summary>
    private static class AgentOptions
    {
        internal static readonly Type OptionsType =
            System.Reflection.Assembly.Load("NutManager.Agent").GetType("NutManager.Agent.NutAgentHttpsOptions")!;

        internal static object Disabled() => Activator.CreateInstance(OptionsType)!;

        internal static object Enabled(string prefix, string thumbprint)
        {
            var options = Activator.CreateInstance(OptionsType)!;
            OptionsType.GetProperty("HttpsEnabled")!.SetValue(options, true);
            OptionsType.GetProperty("HttpsPrefix")!.SetValue(options, prefix);
            OptionsType.GetProperty("CertificateThumbprint")!.SetValue(options, thumbprint);
            return options;
        }

        internal static bool Validate(object options, out string? failure)
        {
            var method = OptionsType.GetMethod("Validate",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var arguments = new[] { options, null };
            var result = (bool)method.Invoke(null, arguments)!;
            failure = arguments[1] as string;
            return result;
        }
    }

    private sealed class RecordingCredentialStore : IRemoteCredentialStore
    {
        public List<RemoteCredentialKind> Reads { get; } = [];

        public string? AgentSecret { get; init; }

        public string? SmbSecret { get; init; }

        public Task<RemoteCredentialStoreResult> ContainsAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.NotFound));

        public Task<RemoteCredentialReadResult> ReadAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default)
        {
            Reads.Add(kind);

            var secret = kind switch
            {
                RemoteCredentialKind.WindowsAgentPassword => AgentSecret,
                RemoteCredentialKind.SmbPassword => SmbSecret,
                _ => null
            };

            return Task.FromResult(secret is null
                ? new RemoteCredentialReadResult(RemoteCredentialStoreStatus.NotFound)
                : new RemoteCredentialReadResult(RemoteCredentialStoreStatus.Success, new RemoteCredentialSecret(secret)));
        }

        public Task<RemoteCredentialStoreResult> WriteAsync(Guid profileId, RemoteCredentialKind kind, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));

        public Task<RemoteCredentialStoreResult> DeleteAsync(Guid profileId, RemoteCredentialKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));

        public Task<RemoteCredentialStoreResult> DeleteAllForProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteCredentialStoreResult(RemoteCredentialStoreStatus.Success));
    }
}
