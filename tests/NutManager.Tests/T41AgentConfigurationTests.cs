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
    public void AgentConfigWindowKeepsTheFixedReferenceComposition()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var codeBehind = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml.cs");

        // 800x600. Both numbers are pinned because the window cannot be resized, so anything that does
        // not fit is unreachable rather than merely cramped - the "Status da configuração" strip is the
        // part that falls off first, and it carries the four owned Windows resources. 4:3 is also the
        // reference composition's own proportion, which is why this fits where 854x480 did not.
        Assert.Contains("Width=\"800\"", window, StringComparison.Ordinal);
        Assert.Contains("Height=\"600\"", window, StringComparison.Ordinal);
        Assert.Contains("CanResize=\"False\"", window, StringComparison.Ordinal);
        Assert.Contains("UseLayoutRounding=\"True\"", window, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource NutSurface1Brush}\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes=\"agent-main-card\"", window, StringComparison.Ordinal);
        Assert.Contains("<Style Selector=\"Border.agent-main-card\">", window, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\" />", window, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"0\" />", window, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\" />", window, StringComparison.Ordinal);
        Assert.Contains("<Grid x:Name=\"AgentMainHeader\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border x:Name=\"AgentMainHeader\"", window, StringComparison.Ordinal);
        Assert.Contains("RenderOptions.BitmapInterpolationMode=\"HighQuality\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentFooter\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConfigurationSurface\"", window, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"0.82*,1.18*\"", window, StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"2\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NamedPipeTransportRow\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HttpsTransportRow\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HttpsEditorFields\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResourceStatusCard\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ContextualActions\"", window, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanToggleNamedPipe}\"", window, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanToggleHttps}\"", window, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding HttpsEnabled}\"", window, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(window, "ColumnDefinitions=\\\"Auto,Auto,\\*,64\\\"").Count);
        Assert.Equal(2, Regex.Matches(window, "Classes=\\\"nut-pill agent-transport-status\\\"").Count);
        Assert.Contains("Classes.critical=\"{Binding !NamedPipeEnabled}\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes.critical=\"{Binding !HttpsEnabled}\"", window, StringComparison.Ordinal);
        Assert.Contains("NutHealthyBrightBrush", window, StringComparison.Ordinal);
        Assert.Contains("NutCriticalBrightBrush", window, StringComparison.Ordinal);
        Assert.Contains("Border.agent-transport-status TextBlock", window, StringComparison.Ordinal);
        Assert.Contains("Property=\"FontWeight\" Value=\"SemiBold\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes.locked=\"{Binding !CanToggleNamedPipe}\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes.locked=\"{Binding !CanToggleHttps}\"", window, StringComparison.Ordinal);
        Assert.Contains("Border.agent-transport-row.locked StackPanel", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Border.agent-transport-row:pointerover", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Border.agent-transport-row:disabled", window, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-disable-https", window, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-reach-badge", window, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentIconLock", window, StringComparison.Ordinal);
        Assert.Contains("AgentIconEye", window, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnImportCertificateClicked\"", window, StringComparison.Ordinal);
        Assert.Contains("Strings[Https.Import]", window, StringComparison.Ordinal);
        Assert.Contains("Strings[Https.Thumbprint]", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ThumbprintField\" Spacing=\"1\"", window, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCopyValueClicked\"", window, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ApplicationVersion}\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes.invalid=\"{Binding HttpsHostHasError}\"", window, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.HelpText=\"{Binding HttpsHostValidationMessage}\"", window, StringComparison.Ordinal);

        var mainCard = window.IndexOf("Classes=\"agent-main-card\"", StringComparison.Ordinal);
        var header = window.IndexOf("x:Name=\"AgentMainHeader\"", StringComparison.Ordinal);
        var configuration = window.IndexOf("x:Name=\"ConfigurationSurface\"", StringComparison.Ordinal);
        var footer = window.IndexOf("x:Name=\"AgentFooter\"", StringComparison.Ordinal);
        Assert.True(mainCard >= 0 && header > mainCard && configuration > header,
            "The product header must be the first region inside the main card.");
        Assert.True(footer >= 0 && footer < mainCard,
            "The footer must remain a separate DockPanel region outside the main card.");

        var httpsCard = window.IndexOf("x:Name=\"HttpsEditorCard\"", StringComparison.Ordinal);
        var httpsFields = window.IndexOf("x:Name=\"HttpsEditorFields\"", StringComparison.Ordinal);
        var httpsHeader = window[httpsCard..httpsFields];
        Assert.DoesNotContain("HttpsStatusText", httpsHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("Https.Disable", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding HttpsValidationMessage}\"", window, StringComparison.Ordinal);

        var thumbprintField = window.IndexOf("x:Name=\"ThumbprintField\"", StringComparison.Ordinal);
        var certificateFeedback = window.IndexOf("IsVisible=\"{Binding ShowCertificateFeedback}\"", thumbprintField, StringComparison.Ordinal);
        var thumbprintMarkup = window[thumbprintField..certificateFeedback];
        Assert.Contains("Text=\"{Binding CertificateThumbprint}\"", thumbprintMarkup, StringComparison.Ordinal);
        Assert.Contains("<TextBlock Classes=\"nut-code\"", thumbprintMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("<TextBox", thumbprintMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCopyValueClicked", thumbprintMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible=", thumbprintMarkup, StringComparison.Ordinal);

        var certificateField = window.IndexOf("Strings[Https.Certificate]", StringComparison.Ordinal);
        var thumbprintLabel = window.IndexOf("x:Name=\"ThumbprintField\"", certificateField, StringComparison.Ordinal);
        var certificateMarkup = window[certificateField..thumbprintLabel];
        Assert.Contains("Classes=\"agent-static-field\"", certificateMarkup, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding SelectedCertificate}\"", certificateMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBox", certificateMarkup, StringComparison.Ordinal);

        Assert.Contains("Width=\"62\"", window, StringComparison.Ordinal);
        Assert.Contains("Height=\"62\"", window, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"NoWrap\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartServiceAction\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-success agent-service-action\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StopServiceAction\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-danger-solid agent-service-action\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RestartServiceAction\"", window, StringComparison.Ordinal);
        Assert.Contains("Button.agent-service-action:pointerover PathIcon", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"nut-icon-refresh\"", window, StringComparison.Ordinal);

        var certificateDetails = window.IndexOf("<!-- ================================================ read-only certificate details -->", StringComparison.Ordinal);
        var confirmationOverlay = window.IndexOf("<!-- ================================================ confirmation overlay -->", certificateDetails, StringComparison.Ordinal);
        var certificateDetailsMarkup = window[certificateDetails..confirmationOverlay];
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", certificateDetailsMarkup, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"165,*\"", certificateDetailsMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CertificateSubject}\" TextWrapping=\"NoWrap\"", certificateDetailsMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CertificateSubjectAlternativeNames}\" TextWrapping=\"NoWrap\"", certificateDetailsMarkup, StringComparison.Ordinal);

        var icons = Read("src/NutManager.Agent.Config/Presentation/AgentConfigIcons.cs");
        Assert.Contains("(\"AgentIconStateError\", MaterialIconKind.CloseCircle)", icons, StringComparison.Ordinal);
        Assert.Contains("(\"AgentIconStateNotConfigured\", MaterialIconKind.MinusCircleOutline)", icons, StringComparison.Ordinal);
        Assert.DoesNotContain("MaterialIconKind.CircleOutline", icons, StringComparison.Ordinal);

        var diagnostics = window.IndexOf("<!-- ================================================ diagnostics -->", StringComparison.Ordinal);
        var firstScrollViewer = window.IndexOf("<ScrollViewer", StringComparison.Ordinal);
        var operators = window.IndexOf("Group administration remains available", StringComparison.Ordinal);
        Assert.True(firstScrollViewer > diagnostics,
            "The fixed 800x600 configuration surface must not depend on a page-level ScrollViewer.");
        Assert.True(diagnostics >= 0 && operators > diagnostics,
            "Operators administration must remain available without changing the reference configuration surface.");

        Assert.Contains("DataTransferItem", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DataFormat.Text", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("SetTextAsync", codeBehind, StringComparison.Ordinal);
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
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentCertificateImporter.cs",
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentRuntimeInventory.cs",
            "src/NutManager.Agent.Config/Views/MainWindow.axaml.cs",
            "src/NutManager.Agent.Config/Views/CertificatePasswordDialog.cs",
        };

        foreach (var file in files)
        {
            var source = WithoutCSharpComments(Read(file));
            foreach (var token in new[]
                     {
                         "Process.Start", "powershell", "pwsh", "cmd.exe", "netsh", "net.exe",
                         "sc.exe", "certutil", "net localgroup", "Start-Service", "Restart-Service", "Stop-Service"
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
    public void FooterVersionComesFromTheAgentConfigAssembly()
    {
        var context = CreateContext();

        Assert.Matches("^v[0-9]+\\.[0-9]+\\.[0-9]+$", context.ViewModel.ApplicationVersion);
    }

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
    public async Task TransportToggleAvailabilityTracksTheLastActiveTransport()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();

        Assert.True(context.ViewModel.NamedPipeEnabled);
        Assert.True(context.ViewModel.HttpsEnabled);
        Assert.True(context.ViewModel.CanToggleNamedPipe);
        Assert.True(context.ViewModel.CanToggleHttps);

        context.ViewModel.HttpsEnabled = false;

        Assert.True(context.ViewModel.NamedPipeEnabled);
        Assert.False(context.ViewModel.CanToggleNamedPipe);
        Assert.True(context.ViewModel.CanToggleHttps);

        context.ViewModel.HttpsEnabled = true;

        Assert.True(context.ViewModel.CanToggleNamedPipe);
        Assert.True(context.ViewModel.CanToggleHttps);

        context.ViewModel.NamedPipeEnabled = false;

        Assert.True(context.ViewModel.CanToggleNamedPipe);
        Assert.False(context.ViewModel.CanToggleHttps);
    }

    [Fact]
    public async Task DisablingHttpsKeepsItsSavedPresentationValuesAvailable()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();
        var endpoint = context.ViewModel.HttpsEndpoint;
        var thumbprint = context.ViewModel.CertificateThumbprint;

        context.ViewModel.HttpsEnabled = false;

        Assert.False(context.ViewModel.HttpsEnabled);
        Assert.Equal(endpoint, context.ViewModel.HttpsEndpoint);
        Assert.Equal(thumbprint, context.ViewModel.CertificateThumbprint);
        Assert.False(context.ViewModel.HttpsIsValid);
        Assert.Null(context.ViewModel.HttpsValidationMessage);
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

    [Fact]
    public async Task ServiceActionsKeepAStableTwoPositionLayout()
    {
        var stopped = CreateContext(serviceState: AgentServiceState.Stopped);
        await stopped.ViewModel.RefreshAsync();

        Assert.True(stopped.ViewModel.ShowStartServiceAction);
        Assert.False(stopped.ViewModel.ShowStopServiceAction);
        Assert.True(stopped.ViewModel.CanStartService);
        Assert.False(stopped.ViewModel.CanRestartService);

        var running = CreateContext(serviceState: AgentServiceState.Running);
        await running.ViewModel.RefreshAsync();

        Assert.False(running.ViewModel.ShowStartServiceAction);
        Assert.True(running.ViewModel.ShowStopServiceAction);
        Assert.True(running.ViewModel.CanStopService);
        Assert.True(running.ViewModel.CanRestartService);

        var absent = CreateContext(serviceState: AgentServiceState.NotInstalled);
        await absent.ViewModel.RefreshAsync();

        // Keep the first slot present, but do not offer an operation for a service that does not exist.
        Assert.True(absent.ViewModel.ShowStartServiceAction);
        Assert.False(absent.ViewModel.ShowStopServiceAction);
        Assert.False(absent.ViewModel.CanStartService);
        Assert.False(absent.ViewModel.CanRestartService);
    }

    [Fact]
    public async Task TheListenerRowSeparatesAnAbsentServiceFromAStoppedOne()
    {
        var absent = CreateContext(serviceState: AgentServiceState.NotInstalled);
        await absent.ViewModel.RefreshAsync();
        absent.ViewModel.HttpsEnabled = true;

        var absentListener = absent.ViewModel.ResourceStatus.Last();
        Assert.Contains("not installed", absentListener.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentDiagnosticState.Error, absentListener.State);

        var stopped = CreateContext(serviceState: AgentServiceState.Stopped);
        await stopped.ViewModel.RefreshAsync();
        stopped.ViewModel.HttpsEnabled = true;

        // Stopped and absent are different machine states and must not collapse into one sentence.
        var stoppedListener = stopped.ViewModel.ResourceStatus.Last();
        Assert.Contains("stopped", stoppedListener.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(absentListener.Detail, stoppedListener.Detail);
    }

    [Fact]
    public async Task MissingExpectedResourcesUseRedErrorsWhileDisabledHttpsStaysMuted()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        Assert.All(context.ViewModel.ResourceStatus, item =>
        {
            Assert.Equal(AgentDiagnosticState.NotConfigured, item.State);
            Assert.Equal("muted", item.StateClass);
            Assert.NotEqual("○", item.Glyph);
        });

        context.ViewModel.HttpsEnabled = true;

        Assert.Equal(AgentDiagnosticState.Error, context.ViewModel.ResourceStatus[0].State);
        Assert.Equal("critical", context.ViewModel.ResourceStatus[0].StateClass);
        Assert.Equal("✕", context.ViewModel.ResourceStatus[0].Glyph);
    }

    [Fact]
    public async Task AnUnusableCertificateReportsOneReasonRatherThanEveryReasonAtOnce()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "other.sbra.local";
        context.ViewModel.HttpsPort = 5199;
        context.ViewModel.SelectedCertificate = Assert.Single(context.ViewModel.Certificates);

        Assert.False(context.ViewModel.HttpsIsValid);

        // One sentence. Three stacked warnings cost three lines of a 600px window and still leave the
        // operator deciding which to act on first.
        var message = Assert.IsType<string>(context.ViewModel.HttpsValidationMessage);
        Assert.DoesNotContain(". ", message.TrimEnd('.'), StringComparison.Ordinal);
        Assert.Contains("other.sbra.local", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostAndPortErrorsUseFieldStateInsteadOfPermanentInlineCopy()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;

        Assert.True(context.ViewModel.HttpsHostHasError);
        Assert.NotNull(context.ViewModel.HttpsHostValidationMessage);
        Assert.False(context.ViewModel.ShowCertificateFeedback);

        context.ViewModel.HttpsHost = "gandalf.sbra.local";
        context.ViewModel.HttpsPort = 0;

        Assert.False(context.ViewModel.HttpsHostHasError);
        Assert.True(context.ViewModel.HttpsPortHasError);
        Assert.Contains("65535", context.ViewModel.HttpsPortValidationMessage, StringComparison.Ordinal);
        Assert.False(context.ViewModel.CanApply);
    }

    [Fact]
    public async Task SuccessfulImportRefreshesTheCatalogAndSelectsTheCertificate()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "gandalf.sbra.local";
        var imported = new AgentCertificateSummary(
            "B909502DD82AE41433E6F83886B00D4277A32A7C",
            "CN=gandalf.sbra.local",
            "CN=Imported Test CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            HasPrivateKey: true,
            SupportsServerAuthentication: true,
            SubjectAlternativeNames: ["gandalf.sbra.local"]);
        context.Importer.Result = AgentCertificateImportResult.Imported(imported);

        var result = await context.ViewModel.ImportCertificateAsync("certificate.pfx", "transient-password");

        Assert.Equal(AgentCertificateImportOutcome.Imported, result.Outcome);
        Assert.Equal(imported.Thumbprint, context.ViewModel.CertificateThumbprint);
        Assert.Contains(context.ViewModel.Certificates, option => option.Thumbprint == imported.Thumbprint);
        Assert.True(context.ViewModel.HttpsIsValid);
        Assert.Equal("healthy", context.ViewModel.CertificateImportStateClass);
        Assert.Contains("imported and selected", context.ViewModel.CertificateImportMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportedButIncompatibleCertificateRemainsInspectableAndBlocksApply()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "gandalf.sbra.local";
        var imported = new AgentCertificateSummary(
            "C909502DD82AE41433E6F83886B00D4277A32A7D",
            "CN=other.sbra.local",
            "CN=Imported Test CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            HasPrivateKey: true,
            SupportsServerAuthentication: true,
            SubjectAlternativeNames: ["other.sbra.local"]);
        context.Importer.Result = AgentCertificateImportResult.Imported(imported);

        await context.ViewModel.ImportCertificateAsync("certificate.cer", password: null);

        Assert.Equal(imported.Thumbprint, context.ViewModel.CertificateThumbprint);
        Assert.False(context.ViewModel.HttpsIsValid);
        Assert.False(context.ViewModel.CanApply);
        Assert.Equal("warning", context.ViewModel.CertificateImportStateClass);
        Assert.Contains("does not name", context.ViewModel.CertificateImportMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncorrectImportPasswordProducesOneLocalizedFailure()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.Importer.Result = AgentCertificateImportResult.From(
            AgentCertificateImportOutcome.PasswordIncorrect);

        var result = await context.ViewModel.ImportCertificateAsync("certificate.pfx", "wrong");

        Assert.Equal(AgentCertificateImportOutcome.PasswordIncorrect, result.Outcome);
        Assert.Equal("critical", context.ViewModel.CertificateImportStateClass);
        Assert.Equal("Incorrect password.", context.ViewModel.CertificateImportMessage);
    }

    [Fact]
    public async Task TheFirewallRowNamesItsPortOnlyWhileHttpsIsOn()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        Assert.DoesNotContain(context.ViewModel.ResourceStatus, item => item.Label.Contains("5199", StringComparison.Ordinal));

        EnableValidHttps(context.ViewModel);

        Assert.Contains(context.ViewModel.ResourceStatus, item => item.Label.Contains("5199", StringComparison.Ordinal));
        Assert.Equal(4, context.ViewModel.ResourceStatus.Count);
        Assert.Equal(Thumbprint, context.ViewModel.CertificateThumbprint);
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
        var importer = new FakeCertificateImporter(certificates);
        var inventory = new FakeInventory();
        var viewModel = new AgentConfigViewModel(
            store, groups, service, resources, certificates, inventory, UiLanguagePreference.EnUs,
            certificateImporter: importer);

        return new TestContext(viewModel, store, groups, service, resources, certificates, importer, events);
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
        FakeCertificates Certificates,
        FakeCertificateImporter Importer,
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
        private readonly List<AgentCertificateSummary> _certificates = [.. certificates];

        public IReadOnlyList<AgentCertificateSummary> List() => _certificates;

        public void Add(AgentCertificateSummary certificate)
        {
            _certificates.RemoveAll(existing => string.Equals(
                existing.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase));
            _certificates.Add(certificate);
        }

        public AgentCertificateSummary? Find(string thumbprint) =>
            _certificates.FirstOrDefault(certificate =>
                string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeCertificateImporter(FakeCertificates certificates) : IAgentCertificateImporter
    {
        public AgentCertificateImportResult Result { get; set; } =
            AgentCertificateImportResult.From(AgentCertificateImportOutcome.Failed);

        public AgentCertificateImportResult Import(string path, string? password)
        {
            if (Result.Certificate is { } certificate) certificates.Add(certificate);
            return Result;
        }
    }

    private sealed class FakeInventory : IAgentRuntimeInventory
    {
        public Task<AgentRuntimeInventorySnapshot> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRuntimeInventorySnapshot("10.0.0", "10.0.0", true, true, "NUT"));
    }
}
