using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Principal;
using NutManager.Agent.Config.Localization;
using NutManager.Agent.Config.ViewModels;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Infrastructure.AgentConfiguration;
using Xunit;

namespace NutManager.Tests;

public sealed class T41InstallerAndPackagingTests
{
    [Fact]
    public void AgentServiceIsAutomaticButInstallationNeverStartsIt()
    {
        var package = Read("installer/Agent/Package.wxs");
        var install = Regex.Match(package, @"<ServiceInstall\b.*?/>", RegexOptions.Singleline).Value;
        var control = Regex.Match(package, @"<ServiceControl\b.*?/>", RegexOptions.Singleline).Value;

        Assert.Contains("Start=\"auto\"", install, StringComparison.Ordinal);
        Assert.DoesNotContain("Start=", control, StringComparison.Ordinal);
        Assert.Contains("Stop=\"both\"", control, StringComparison.Ordinal);
        Assert.Contains("Remove=\"uninstall\"", control, StringComparison.Ordinal);
        Assert.Contains("Name=\"$(AgentServiceName)\"", control, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerControlsOnlyNutManagerAgentAndNeverNut()
    {
        var package = WithoutComments(Read("installer/Agent/Package.wxs"));
        var serviceNames = Regex.Matches(package, @"<Service(?:Install|Control)\b[^>]*\bName=""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(serviceNames);
        Assert.All(serviceNames, name => Assert.Equal("$(AgentServiceName)", name));
        Assert.DoesNotContain("Start-Service", package, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restart-Service", package, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Service", package, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentConfigIsPublishedAndOwnedByTheAgentMsi()
    {
        var script = Read("scripts/build-release.ps1");
        var package = Read("installer/Agent/Package.wxs");

        Assert.Contains("src\\NutManager.Agent.Config\\NutManager.Agent.Config.csproj", script, StringComparison.Ordinal);
        Assert.Contains("NutManager.Agent.Config.exe", script, StringComparison.Ordinal);
        Assert.Contains("NutManager.Agent.Config.exe", package, StringComparison.Ordinal);
        Assert.Contains("AgentConfigStartMenuShortcut", package, StringComparison.Ordinal);
        Assert.Contains("Target=\"[#AgentConfigExecutableFile]\"", package, StringComparison.Ordinal);
        Assert.Contains("On=\"uninstall\"", package, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentConfigurationFileRemainsOutsideInstallerOwnership()
    {
        Assert.DoesNotContain(
            "agent.json",
            WithoutComments(Read("installer/Agent/Package.wxs")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryAgentBundleBuildPathIncludesOnlyTheRequiredWixExtensions()
    {
        var script = Read("scripts/build-release.ps1");
        var workflow = Read(".github/workflows/package.yml");

        foreach (var extension in new[]
                 {
                     "WixToolset.BootstrapperApplications.wixext",
                     "WixToolset.Netfx.wixext",
                     "WixToolset.Util.wixext"
                 })
        {
            Assert.Contains(extension, script, StringComparison.Ordinal);
            Assert.Contains($"{extension}/5.0.2", workflow, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("WixToolset.Util.wixext.dll", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentConfigStaysFrameworkDependentAndCannotAddWindowsDesktop()
    {
        var project = Read("src/NutManager.Agent.Config/NutManager.Agent.Config.csproj");
        var script = Read("scripts/build-release.ps1");

        Assert.Contains("<SelfContained>false</SelfContained>", project, StringComparison.Ordinal);
        var authoredProject = WithoutComments(project);
        Assert.DoesNotContain("Microsoft.WindowsDesktop.App", authoredProject, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWPF", authoredProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWindowsForms", authoredProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Agent Config introduced an unsupported shared framework", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NETCore.App", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseStagingRejectsPrivateRuntimesAndRemovesDebugSymbols()
    {
        var script = Read("scripts/build-release.ps1");

        foreach (var marker in new[] { "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll", "Microsoft.AspNetCore.dll" })
        {
            Assert.Contains(marker, script, StringComparison.Ordinal);
        }

        Assert.Contains("-Filter '*.pdb'", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -Force", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerProgressUsesSupportedWixStdBaControlsAndLocalizedPhases()
    {
        var theme = Read("installer/Common/Theme/AgentTheme.xml");
        var portuguese = Read("installer/Common/Theme/Agent.pt-BR.wxl");
        var english = Read("installer/Common/Theme/Agent.en-US.wxl");

        foreach (var control in new[]
                 {
                     "CacheProgressPackageText",
                     "CacheProgressbar",
                     "ExecuteProgressPackageText",
                     "ExecuteProgressbar",
                     "ExecuteProgressText",
                     "ExecuteProgressActionDataText",
                     "OverallCalculatedProgressbar"
                 })
        {
            Assert.Contains($"Name=\"{control}\"", theme, StringComparison.Ordinal);
        }

        Assert.Contains("Name=\"ShowProgressDetails\"", theme, StringComparison.Ordinal);
        Assert.Contains("Variable Name=\"ShowProgressDetails\"", Read("installer/Agent/Bundle.wxs"), StringComparison.Ordinal);
        Assert.Contains("Baixando:", portuguese, StringComparison.Ordinal);
        Assert.Contains("Instalando:", portuguese, StringComparison.Ordinal);
        Assert.DoesNotContain("Processando:", portuguese, StringComparison.Ordinal);
        Assert.Contains("Downloading:", english, StringComparison.Ordinal);
        Assert.Contains("Installing:", english, StringComparison.Ordinal);
        Assert.DoesNotContain("BootstrapperApplicationRef", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessPageReportsOnlySupportedEvidenceAndDelegatesOperatorsDiagnostics()
    {
        var portuguese = WithoutComments(Read("installer/Common/Theme/Agent.pt-BR.wxl"));
        var english = WithoutComments(Read("installer/Common/Theme/Agent.en-US.wxl"));

        Assert.DoesNotContain("Estado: Parado", portuguese, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTPS não configurado", portuguese, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Operators encontrado", portuguese, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NutManager Agent Config", portuguese, StringComparison.Ordinal);
        Assert.Contains("NutManager Agent Config", english, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentConfigWindowUsesTheSharedProductIcon()
    {
        var project = Read("src/NutManager.Agent.Config/NutManager.Agent.Config.csproj");
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        Assert.Contains("NutManager.ico", project, StringComparison.Ordinal);
        Assert.Contains("Link=\"Assets\\Branding\\NutManager.ico\"", project, StringComparison.Ordinal);
        Assert.Contains("Icon=\"/Assets/Branding/NutManager.ico\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("E:\\PROJECTS", window, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentStartupFailsClosedWhenAHandEditedFileDisablesBothTransports()
    {
        var service = Read("src/NutManager.Agent/NutAgentWindowsService.cs");

        Assert.Contains("if (!namedPipeEnabled && !options.HttpsEnabled)", service, StringComparison.Ordinal);
        Assert.Contains("FailToStart();", service, StringComparison.Ordinal);
        Assert.Contains("if (namedPipeEnabled)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentConfigLocalizationsHaveExactParityAndKeepCanonicalOperatorCopy()
    {
        var portugueseKeys = AgentConfigStrings.KeysFor(UiLanguagePreference.PtBr);
        var englishKeys = AgentConfigStrings.KeysFor(UiLanguagePreference.EnUs);
        var portuguese = new AgentConfigStrings(UiLanguagePreference.PtBr);
        var english = new AgentConfigStrings(UiLanguagePreference.EnUs);

        Assert.Equal(portugueseKeys.Order(), englishKeys.Order());
        Assert.Equal("Adicionar usuário", portuguese["Operators.AddUser"]);
        Assert.Equal("NutManager Operators", portuguese["Operators.Title"]);
        Assert.Equal("NutManager Operators", english["Operators.Title"]);
    }

    [Fact]
    public void NewAdministrativeRuntimeCodeIntroducesNoGenericShellExecution()
    {
        var files = new[]
        {
            "src/NutManager.Agent.Config/Program.cs",
            "src/NutManager.Agent.Config/App.axaml.cs",
            "src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs",
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentServiceAdministration.cs",
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentOperatorsGroupAdministration.cs",
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentHttpsResourceAdministration.cs",
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentConfigurationStore.cs",
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentCertificateCatalog.cs",
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentRuntimeInventory.cs",
        };

        foreach (var file in files)
        {
            var source = WithoutCSharpComments(Read(file));
            foreach (var token in new[]
                     {
                         "Process.Start", "powershell", "pwsh", "cmd.exe", "netsh", "net.exe",
                         "sc.exe", "net localgroup", "Start-Service", "Restart-Service", "Stop-Service"
                     })
            {
                Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "NutManager.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("NutManager repository root was not found.");
    }

    private static string WithoutComments(string source) =>
        Regex.Replace(source, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string WithoutCSharpComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//.*$", string.Empty, RegexOptions.Multiline);
    }
}

public sealed class T41AgentTransportAndCertificateTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EveryNonEmptyTransportCombinationIsValid(bool namedPipe, bool https)
    {
        var selection = AgentTransportSelection.Create(namedPipe, https);

        Assert.Equal(namedPipe, selection.NamedPipeEnabled);
        Assert.Equal(https, selection.HttpsEnabled);
    }

    [Fact]
    public void BothTransportsDisabledIsRejectedByTheSharedRule()
    {
        var document = new AgentTransportConfigurationDocument
        {
            NamedPipeEnabled = false,
            HttpsEnabled = false,
        };

        Assert.False(AgentTransportSelection.TryCreate(false, false, out var selection, out _));
        Assert.Null(selection);
        Assert.False(AgentTransportConfigurationDocument.Validate(document, out var failure));
        Assert.Contains("Both transports", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoConfigurationAndLegacyConfigurationKeepTheNamedPipeEnabled()
    {
        var missing = new AgentTransportConfigurationDocument();
        var legacy = JsonSerializer.Deserialize<AgentTransportConfigurationDocument>(
            """{"httpsEnabled":false}""",
            AgentTransportConfigurationDocument.SerializerOptions)!;

        Assert.True(missing.NamedPipeIsEnabled);
        Assert.True(legacy.NamedPipeIsEnabled);
        Assert.False(legacy.HttpsEnabled);
    }

    [Fact]
    public void CanonicalConfigurationPreservesAnExplicitDisabledNamedPipe()
    {
        var document = new AgentTransportConfigurationDocument
        {
            NamedPipeEnabled = false,
            HttpsEnabled = true,
            HttpsPrefix = "https://gandalf.sbra.local:5199/",
            CertificateThumbprint = Thumbprint,
        };

        var json = JsonSerializer.Serialize(document, AgentTransportConfigurationDocument.SerializerOptions);
        var roundTrip = JsonSerializer.Deserialize<AgentTransportConfigurationDocument>(
            json, AgentTransportConfigurationDocument.SerializerOptions)!;

        Assert.False(roundTrip.NamedPipeIsEnabled);
        Assert.True(AgentTransportConfigurationDocument.Validate(roundTrip, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void ValidCertificateIsAccepted()
    {
        var verdict = AgentCertificateRules.Evaluate(Certificate(), Host, Now);

        Assert.True(verdict.IsUsable);
        Assert.Empty(verdict.Problems);
    }

    [Fact]
    public void CertificateWithoutPrivateKeyIsRejected()
    {
        var verdict = AgentCertificateRules.Evaluate(Certificate(hasPrivateKey: false), Host, Now);

        Assert.False(verdict.IsUsable);
        Assert.Contains(verdict.Problems, problem => problem.Contains("private key", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-10, -1, "expired")]
    [InlineData(1, 10, "not valid until")]
    public void CertificateValidityWindowIsEnforced(int startsInDays, int endsInDays, string expected)
    {
        var certificate = Certificate(notBefore: Now.AddDays(startsInDays), notAfter: Now.AddDays(endsInDays));

        var verdict = AgentCertificateRules.Evaluate(certificate, Host, Now);

        Assert.False(verdict.IsUsable);
        Assert.Contains(verdict.Problems, problem => problem.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServerAuthenticationEkuIsRequired()
    {
        var verdict = AgentCertificateRules.Evaluate(Certificate(supportsServerAuthentication: false), Host, Now);

        Assert.False(verdict.IsUsable);
        Assert.Contains(verdict.Problems, problem => problem.Contains("server authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("gandalf.sbra.local", "gandalf.sbra.local", true)]
    [InlineData("*.sbra.local", "gandalf.sbra.local", true)]
    [InlineData("*.sbra.local", "a.gandalf.sbra.local", false)]
    [InlineData("*.sbra.local", "sbra.local", false)]
    [InlineData("other.sbra.local", "gandalf.sbra.local", false)]
    public void SubjectAlternativeNameMatchingHonorsSingleLabelWildcards(string certificateName, string host, bool expected)
    {
        Assert.Equal(expected, AgentCertificateRules.MatchesHost(Certificate(names: [certificateName]), host));
    }

    [Fact]
    public void CleanupContractHasNoCertificateDeletionCapability()
    {
        var properties = typeof(AgentHttpsCleanupRequest)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(["RemoveFirewallRule", "RemoveSslBinding", "RemoveUrlReservation", "RemovesAnything"], properties);
        Assert.DoesNotContain(properties, name => name.Contains("Certificate", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AgentResourceOwnership.OwnedByNutManager, true)]
    [InlineData(AgentResourceOwnership.ForeignOwner, false)]
    [InlineData(AgentResourceOwnership.Unknown, false)]
    [InlineData(AgentResourceOwnership.Absent, false)]
    public void OnlyProvablyOwnedResourcesMayBeRemoved(AgentResourceOwnership ownership, bool expected)
    {
        Assert.Equal(expected, new AgentResourceState(ownership).MayRemove);
    }

    [Theory]
    [InlineData(AgentResourceOwnership.Absent, true)]
    [InlineData(AgentResourceOwnership.OwnedByNutManager, true)]
    [InlineData(AgentResourceOwnership.ForeignOwner, false)]
    [InlineData(AgentResourceOwnership.Unknown, false)]
    public void ApplyConfiguresOnlyAbsentOrProvablyOwnedResources(AgentResourceOwnership ownership, bool expected)
    {
        Assert.Equal(expected, new AgentResourceState(ownership).MayConfigure);
    }

    [Theory]
    [InlineData(AgentPrincipalKind.User, true)]
    [InlineData(AgentPrincipalKind.Group, true)]
    [InlineData(AgentPrincipalKind.Alias, true)]
    [InlineData(AgentPrincipalKind.Computer, false)]
    [InlineData(AgentPrincipalKind.Domain, false)]
    [InlineData(AgentPrincipalKind.DeletedAccount, false)]
    public void OnlyResolvedUsersAndGroupsCanBecomeOperators(AgentPrincipalKind kind, bool expected)
    {
        var resolution = new AgentIdentityResolution(true, "principal", "S-1-5-21-1000", kind, "SBRA", null);

        Assert.Equal(expected, resolution.IsAddable);
    }

    [Theory]
    [InlineData(AgentMachineRole.StandaloneWorkstation, false)]
    [InlineData(AgentMachineRole.MemberServer, false)]
    [InlineData(AgentMachineRole.DomainController, true)]
    [InlineData(AgentMachineRole.Unknown, true)]
    public void DomainAndUnknownRolesRequireDirectoryConfirmation(AgentMachineRole role, bool expected)
    {
        var state = AgentOperatorsGroupState.Missing("NutManager Operators", role);

        Assert.Equal(expected, state.CreationAffectsDirectory);
    }

    private const string Host = "gandalf.sbra.local";
    private const string Thumbprint = "A909502DD82AE41433E6F83886B00D4277A32A7B";
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static AgentCertificateSummary Certificate(
        bool hasPrivateKey = true,
        bool supportsServerAuthentication = true,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        IReadOnlyList<string>? names = null) =>
        new(
            Thumbprint,
            $"CN={Host}",
            "CN=NutManager Test CA",
            notBefore ?? Now.AddDays(-1),
            notAfter ?? Now.AddDays(30),
            hasPrivateKey,
            supportsServerAuthentication,
            names ?? [Host]);
}

public sealed class T41AgentConfigurationStoreTests
{
    [Fact]
    public void MissingFileReadsAsNamedPipeOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TemporaryDirectory();
        var store = new WindowsAgentConfigurationStore(Path.Combine(directory.Path, "agent.json"));

        var document = store.Read();

        Assert.True(document.NamedPipeIsEnabled);
        Assert.False(document.HttpsEnabled);
    }

    [Fact]
    public void InvalidDocumentIsRejectedBeforeExistingFileChanges()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "agent.json");
        File.WriteAllText(path, "original");
        var store = new WindowsAgentConfigurationStore(path);

        var result = store.Write(new AgentTransportConfigurationDocument
        {
            NamedPipeEnabled = false,
            HttpsEnabled = false,
        });

        Assert.False(result.Succeeded);
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void MalformedConfigurationFallsBackToNamedPipeOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "agent.json");
        File.WriteAllText(path, "{ not-json");
        var store = new WindowsAgentConfigurationStore(path);

        var document = store.Read();

        Assert.True(document.NamedPipeIsEnabled);
        Assert.False(document.HttpsEnabled);
    }

    [Fact]
    public void ValidWriteProducesCanonicalReadableJsonAndLeavesNoTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsElevated()) return;

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "agent.json");
        var store = new WindowsAgentConfigurationStore(path);
        var document = new AgentTransportConfigurationDocument
        {
            NamedPipeEnabled = true,
            HttpsEnabled = false,
        };

        var result = store.Write(document);

        Assert.True(result.Succeeded, result.Failure);
        Assert.True(store.Exists);
        Assert.True(store.Read().NamedPipeIsEnabled);
        Assert.Contains("\"namedPipeEnabled\": true", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NutManager-T41-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

public sealed class T41AgentConfigViewModelTests
{
    [Fact]
    public async Task RefreshUsesLegacyNamedPipeDefault()
    {
        var context = CreateContext();

        await context.ViewModel.RefreshAsync();

        Assert.True(context.ViewModel.NamedPipeEnabled);
        Assert.False(context.ViewModel.HttpsEnabled);
        Assert.False(context.ViewModel.IsDirty);
    }

    [Fact]
    public async Task LastEnabledTransportCannotBeDisabled()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        context.ViewModel.NamedPipeEnabled = false;

        Assert.True(context.ViewModel.NamedPipeEnabled);
        Assert.False(context.ViewModel.CanToggleNamedPipe);
        Assert.True(context.ViewModel.ShowsLastTransportNotice);
    }

    [Fact]
    public async Task NamedPipeCanBeDisabledAfterHttpsBecomesValid()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        EnableValidHttps(context.ViewModel);
        context.ViewModel.NamedPipeEnabled = false;

        Assert.False(context.ViewModel.NamedPipeEnabled);
        Assert.True(context.ViewModel.HttpsEnabled);
        Assert.True(context.ViewModel.CanApply);
    }

    [Fact]
    public async Task InvalidHttpsPreventsApplyEvenWhenTheUiStateIsDirty()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "wrong.sbra.local";

        Assert.True(context.ViewModel.IsDirty);
        Assert.False(context.ViewModel.CanApply);
        await context.ViewModel.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(context.Store.Writes);
        Assert.Equal(0, context.Resources.ApplyCalls);
    }

    [Fact]
    public async Task FailedSystemResourceApplyDoesNotCommitConfiguration()
    {
        var context = CreateContext();
        context.Resources.ApplyResult = AgentHttpsResourceResult.Failed("binding failed");
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(context.Store.Writes);
        Assert.True(context.ViewModel.ApplyFailed);
        Assert.Contains("binding failed", context.ViewModel.ApplyMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulApplyWritesCanonicalTransportAfterResources()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);
        context.ViewModel.NamedPipeEnabled = false;

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        var written = Assert.Single(context.Store.Writes);
        Assert.False(written.NamedPipeIsEnabled);
        Assert.True(written.HttpsEnabled);
        Assert.Equal("https://gandalf.sbra.local:5199/", written.HttpsPrefix);
        Assert.Equal(["resources.apply", "store.write"], context.Events);
    }

    [Fact]
    public async Task ApplyNeverStartsAStoppedAgent()
    {
        var context = CreateContext(serviceState: AgentServiceState.Stopped);
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, context.Service.StartCalls);
        Assert.Equal(0, context.Service.RestartCalls);
        Assert.Equal(AgentConfigConfirmation.None, context.ViewModel.PendingConfirmation);
    }

    [Fact]
    public async Task RunningAgentRestartIsOfferedAndOccursOnlyAfterConfirmation()
    {
        var context = CreateContext(serviceState: AgentServiceState.Running);
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, context.Service.RestartCalls);
        Assert.Equal(AgentConfigConfirmation.RestartService, context.ViewModel.PendingConfirmation);

        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, context.Service.RestartCalls);
    }

    [Fact]
    public async Task DomainControllerGroupCreationRequiresExplicitConfirmation()
    {
        var context = CreateContext(groupRole: AgentMachineRole.DomainController);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.CreateGroupCommand.Execute(null);

        Assert.Equal(0, context.Groups.CreateCalls);
        Assert.Equal(AgentConfigConfirmation.CreateGroupInDirectory, context.ViewModel.PendingConfirmation);

        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, context.Groups.CreateCalls);
    }

    [Fact]
    public async Task WorkstationGroupCreationIsExplicitButNeedsNoDirectoryConfirmation()
    {
        var context = CreateContext(groupRole: AgentMachineRole.StandaloneWorkstation);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.CreateGroupCommand.Execute(null);

        Assert.Equal(1, context.Groups.CreateCalls);
        Assert.Equal(AgentConfigConfirmation.None, context.ViewModel.PendingConfirmation);
    }

    [Fact]
    public async Task ExistingMemberIsASuccessfulIdempotentOutcome()
    {
        var context = CreateContext(groupExists: true);
        context.Groups.AddResult = new AgentMembershipResult(
            AgentMembershipOutcome.AlreadyMember, @"SBRA\operator");
        await context.ViewModel.RefreshAsync();
        context.ViewModel.NewMemberAccount = @"SBRA\operator";

        context.ViewModel.AddMemberCommand.Execute(null);

        Assert.Equal(1, context.Groups.AddCalls);
        Assert.Equal(string.Empty, context.ViewModel.NewMemberAccount);
    }

    [Fact]
    public async Task DisablingHttpsRemovesOnlyExplicitlySelectedOwnedResources()
    {
        var document = HttpsDocument();
        var context = CreateContext(document: document);
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.ForeignOwner),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager));
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = false;

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(AgentConfigConfirmation.DisableHttps, context.ViewModel.PendingConfirmation);

        context.ViewModel.CleanupFirewallRule = true;
        context.ViewModel.CleanupSslBinding = false;
        context.ViewModel.CleanupUrlReservation = true;
        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);

        var cleanup = Assert.Single(context.Resources.RemoveRequests);
        Assert.True(cleanup.RemoveFirewallRule);
        Assert.False(cleanup.RemoveSslBinding);
        Assert.True(cleanup.RemoveUrlReservation);
        Assert.False(Assert.Single(context.Store.Writes).HttpsEnabled);
    }

    [Fact]
    public async Task ForeignAndUnknownResourcesNeverTriggerRemoval()
    {
        var context = CreateContext(document: HttpsDocument());
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.ForeignOwner),
            new AgentResourceState(AgentResourceOwnership.Unknown),
            AgentResourceState.Absent);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = false;

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(AgentConfigConfirmation.None, context.ViewModel.PendingConfirmation);
        Assert.Empty(context.Resources.RemoveRequests);
        Assert.False(Assert.Single(context.Store.Writes).HttpsEnabled);
    }

    private const string Thumbprint = "A909502DD82AE41433E6F83886B00D4277A32A7B";

    private static TestContext CreateContext(
        AgentTransportConfigurationDocument? document = null,
        AgentServiceState serviceState = AgentServiceState.Stopped,
        AgentMachineRole groupRole = AgentMachineRole.StandaloneWorkstation,
        bool groupExists = false)
    {
        var events = new List<string>();
        var store = new FakeStore(document ?? new AgentTransportConfigurationDocument(), events);
        var groups = new FakeGroups(groupRole, groupExists);
        var service = new FakeService(serviceState);
        var resources = new FakeResources(events);
        var certificate = new AgentCertificateSummary(
            Thumbprint,
            "CN=gandalf.sbra.local",
            "CN=Test CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            HasPrivateKey: true,
            SupportsServerAuthentication: true,
            SubjectAlternativeNames: ["gandalf.sbra.local"]);
        var certificates = new FakeCertificates(certificate);
        var inventory = new FakeInventory();
        var viewModel = new AgentConfigViewModel(
            store, groups, service, resources, certificates, inventory, UiLanguagePreference.EnUs);

        return new TestContext(viewModel, store, groups, service, resources, events);
    }

    private static void EnableValidHttps(AgentConfigViewModel viewModel)
    {
        viewModel.HttpsEnabled = true;
        viewModel.HttpsHost = "gandalf.sbra.local";
        viewModel.HttpsPort = 5199;
        viewModel.SelectedCertificate = Assert.Single(viewModel.Certificates);
        Assert.True(viewModel.HttpsIsValid, viewModel.HttpsValidationMessage);
    }

    private static AgentTransportConfigurationDocument HttpsDocument() => new()
    {
        NamedPipeEnabled = true,
        HttpsEnabled = true,
        HttpsPrefix = "https://gandalf.sbra.local:5199/",
        CertificateThumbprint = Thumbprint,
    };

    private sealed record TestContext(
        AgentConfigViewModel ViewModel,
        FakeStore Store,
        FakeGroups Groups,
        FakeService Service,
        FakeResources Resources,
        List<string> Events);

    private sealed class FakeStore(AgentTransportConfigurationDocument document, List<string> events)
        : IAgentConfigurationStore
    {
        public List<AgentTransportConfigurationDocument> Writes { get; } = [];
        public AgentConfigurationWriteResult WriteResult { get; set; } = AgentConfigurationWriteResult.Success;
        public string Path => "agent.json";
        public bool Exists => true;
        public AgentTransportConfigurationDocument Read() => document;

        public AgentConfigurationWriteResult Write(AgentTransportConfigurationDocument value)
        {
            events.Add("store.write");
            Writes.Add(value);
            return WriteResult;
        }
    }

    private sealed class FakeGroups(AgentMachineRole role, bool exists) : IAgentOperatorsGroupAdministration
    {
        public int CreateCalls { get; private set; }
        public int AddCalls { get; private set; }
        public AgentMembershipResult AddResult { get; set; } =
            new(AgentMembershipOutcome.Added, @"SBRA\operator");

        public AgentOperatorsGroupState Describe() =>
            new(exists || CreateCalls > 0, "NutManager Operators", CreateCalls > 0 || exists ? "S-1-5-32-1000" : null, role, null);

        public AgentGroupCreationResult Create()
        {
            CreateCalls++;
            return new AgentGroupCreationResult(true, "NutManager Operators", "S-1-5-32-1000", null);
        }

        public AgentIdentityResolution ResolveIdentity(string accountName) =>
            new(true, accountName, "S-1-5-21-1000", AgentPrincipalKind.User, "SBRA", null);

        public AgentMembershipResult AddMember(string accountName)
        {
            AddCalls++;
            return AddResult with { AccountName = accountName };
        }

        public IReadOnlyList<string> ListMembers() => exists ? [@"SBRA\operator"] : [];
    }

    private sealed class FakeService(AgentServiceState state) : IAgentServiceAdministration
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int RestartCalls { get; private set; }

        public AgentServiceSnapshot Describe() => new(state, "Automatic", null);

        public Task<AgentServiceOutcome> StartAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.FromResult(new AgentServiceOutcome(true, AgentServiceState.Running, null));
        }

        public Task<AgentServiceOutcome> StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.FromResult(new AgentServiceOutcome(true, AgentServiceState.Stopped, null));
        }

        public Task<AgentServiceOutcome> RestartAsync(CancellationToken cancellationToken)
        {
            RestartCalls++;
            return Task.FromResult(new AgentServiceOutcome(true, AgentServiceState.Running, null));
        }
    }

    private sealed class FakeResources(List<string> events) : IAgentHttpsResourceAdministration
    {
        public AgentHttpsResourceSnapshot Snapshot { get; set; } = AgentHttpsResourceSnapshot.None;
        public AgentHttpsResourceResult ApplyResult { get; set; } = AgentHttpsResourceResult.Success([]);
        public int ApplyCalls { get; private set; }
        public List<AgentHttpsCleanupRequest> RemoveRequests { get; } = [];

        public AgentHttpsResourceSnapshot Describe(AgentHttpsBinding binding) => Snapshot;

        public AgentHttpsResourceResult Apply(AgentHttpsBinding binding)
        {
            ApplyCalls++;
            events.Add("resources.apply");
            return ApplyResult;
        }

        public AgentHttpsResourceResult Remove(AgentHttpsBinding binding, AgentHttpsCleanupRequest request)
        {
            RemoveRequests.Add(request);
            return AgentHttpsResourceResult.Success([]);
        }
    }

    private sealed class FakeCertificates(params AgentCertificateSummary[] certificates) : IAgentCertificateCatalog
    {
        public IReadOnlyList<AgentCertificateSummary> List() => certificates;

        public AgentCertificateSummary? Find(string thumbprint) =>
            certificates.FirstOrDefault(certificate =>
                string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeInventory : IAgentRuntimeInventory
    {
        public Task<AgentRuntimeInventorySnapshot> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRuntimeInventorySnapshot("10.0.0", "10.0.0", true, true, "NUT"));
    }
}
