using System.Collections.Specialized;
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
    public void TheAgentMsiInstallsOneExecutableAndStartsItInTwoModes()
    {
        // The service and the configuration window are one file now, told apart by one argument, and the
        // installer is where that has to be exact. A ServiceInstall without --service would register a
        // service that tries to open a window in session 0, and a shortcut without --config would hand an
        // operator a shortcut that starts a service host with no console and nothing to show.
        var script = Read("scripts/build-release.ps1");
        var package = Read("installer/Agent/Package.wxs");

        Assert.DoesNotContain("NutManager.Agent.Config.csproj", script, StringComparison.Ordinal);
        // The release script may still name the retired executable, but only in order to refuse it.
        Assert.Contains("retired Agent Config apphost file", script, StringComparison.Ordinal);
        Assert.Contains("must contain exactly one executable", script, StringComparison.Ordinal);
        var authoredPackage = WithoutComments(package);
        Assert.DoesNotContain("NutManager.Agent.Config.exe", authoredPackage, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentConfigExecutableFile", authoredPackage, StringComparison.Ordinal);

        Assert.Contains("Arguments=\"--service\"", package, StringComparison.Ordinal);
        Assert.Contains("AgentConfigStartMenuShortcut", package, StringComparison.Ordinal);
        Assert.Contains("Target=\"[#AgentExecutableFile]\"", package, StringComparison.Ordinal);
        Assert.Contains("Arguments=\"--config\"", package, StringComparison.Ordinal);
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
    public void AgentConfigIsALibraryInsideTheAgentHostAndAddsNoSharedFramework()
    {
        // The configuration window has no apphost of its own any more: it is a module the agent host
        // starts. The WindowsDesktop guard outlives that change, because WPF or WinForms here would still
        // add a third shared framework the Agent installer neither detects nor downloads.
        var project = Read("src/NutManager.Agent.Config/NutManager.Agent.Config.csproj");
        var host = Read("src/NutManager.Agent/NutManager.Agent.csproj");
        var script = Read("scripts/build-release.ps1");

        var authoredProject = WithoutComments(project);
        Assert.Contains("<OutputType>Library</OutputType>", authoredProject, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicationManifest", authoredProject, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>false</SelfContained>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.WindowsDesktop.App", authoredProject, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWPF", authoredProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWindowsForms", authoredProject, StringComparison.OrdinalIgnoreCase);

        // The window is launched by hand, so the one apphost must not be a console binary, and the
        // elevation manifest has to follow the apphost rather than stay with the library.
        var authoredHost = WithoutComments(host);
        Assert.Contains("<OutputType>WinExe</OutputType>", authoredHost, StringComparison.Ordinal);
        Assert.Contains("app.manifest", authoredHost, StringComparison.Ordinal);
        Assert.Contains("NutManager.Agent.Config.csproj", authoredHost, StringComparison.Ordinal);

        Assert.Contains("must contain exactly one executable", script, StringComparison.Ordinal);
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
    public void PortableAgentTestPackageIsAValidatedBinaryOnlyPublish()
    {
        var script = Read("scripts/build-agent-test-package.ps1");

        Assert.Contains("src\\NutManager.Agent\\NutManager.Agent.csproj", script, StringComparison.Ordinal);
        // One publish, one apphost. The second publish and the hash-reconciled merge that used to follow
        // it existed only to get two executables into one directory.
        Assert.DoesNotContain("NutManager.Agent.Config.csproj", script, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, "dotnet publish", RegexOptions.IgnoreCase));
        Assert.Single(Regex.Matches(script, "--self-contained false", RegexOptions.IgnoreCase));
        Assert.Contains("GetRelativePath", script, StringComparison.Ordinal);
        Assert.Contains("NutManager.Agent.Config.exe", script, StringComparison.Ordinal);
        Assert.Contains("must contain exactly one executable", script, StringComparison.Ordinal);
        Assert.Contains("NutManager.Agent.Config.dll", script, StringComparison.Ordinal);

        foreach (var marker in new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll" })
        {
            Assert.Contains(marker, script, StringComparison.Ordinal);
        }

        Assert.Contains("$source.Extension -eq '.pdb'", script, StringComparison.Ordinal);
        Assert.Contains("agent.json", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Compress-Archive", script, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", script, StringComparison.Ordinal);
        Assert.Contains("NutManager-Agent-Test-$Version", script, StringComparison.Ordinal);
        Assert.Contains("$packageName.zip", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Service", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Service", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restart-Service", script, StringComparison.OrdinalIgnoreCase);
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
        // The field, not its exact one-line spelling: it now also carries the visibility binding
        // that collapses it when no certificate is selected.
        Assert.Contains("x:Name=\"ThumbprintField\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CertificateFeedbackRow\"", window, StringComparison.Ordinal);
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

        // Source order is no longer visual order: the validation row is declared before the field
        // stack and docked to the bottom of the card, which is what stops a two-line message pushing
        // itself past the card edge. Each block is therefore located on its own.
        var thumbprintField = window.IndexOf("x:Name=\"ThumbprintField\"", StringComparison.Ordinal);
        var thumbprintEnd = window.IndexOf("</Grid>", thumbprintField, StringComparison.Ordinal);
        var thumbprintMarkup = window[thumbprintField..thumbprintEnd];

        var certificateFeedback = window.IndexOf("x:Name=\"CertificateFeedbackRow\"", StringComparison.Ordinal);
        var feedbackEnd = window.IndexOf("</Grid>", certificateFeedback, StringComparison.Ordinal);
        var feedbackMarkup = window[certificateFeedback..feedbackEnd];

        Assert.True(certificateFeedback >= 0, "Certificate validation must occupy its own row.");
        Assert.Contains("DockPanel.Dock=\"Bottom\"",
            window[(certificateFeedback - 200)..certificateFeedback], StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CertificateThumbprint}\"", thumbprintMarkup, StringComparison.Ordinal);
        // Rendered as code rather than as an editable field. The class, not its exact spelling:
        // the element now also carries a Grid.Column since the label sits beside the value.
        Assert.Contains("Classes=\"nut-code\"", thumbprintMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("<TextBox", thumbprintMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCopyValueClicked", thumbprintMarkup, StringComparison.Ordinal);
        // The block collapses as a whole and its parts never do. Exactly one visibility binding, on
        // the container: hiding the value while keeping the label would leave a heading over nothing,
        // which is the state that read as a thumbprint that failed to load.
        Assert.Equal(
            1,
            thumbprintMarkup.Split("IsVisible=", StringSplitOptions.None).Length - 1);
        Assert.Contains("IsVisible=\"{Binding ShowThumbprint}\"", thumbprintMarkup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowCertificateFeedback}\"", feedbackMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CertificateFeedbackMessage}\"", feedbackMarkup, StringComparison.Ordinal);

        var certificateField = window.IndexOf("Strings[Https.Certificate]", StringComparison.Ordinal);
        var thumbprintLabel = window.IndexOf("x:Name=\"ThumbprintField\"", certificateField, StringComparison.Ordinal);
        var certificateMarkup = window[certificateField..thumbprintLabel];
        Assert.Contains("Classes=\"agent-static-field\"", certificateMarkup, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding SelectedCertificate}\"", certificateMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBox", certificateMarkup, StringComparison.Ordinal);

        Assert.Contains("Width=\"78\"", window, StringComparison.Ordinal);
        Assert.Contains("Height=\"78\"", window, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"NoWrap\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartServiceAction\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-success agent-service-action\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StopServiceAction\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-danger-solid agent-service-action\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RestartServiceAction\"", window, StringComparison.Ordinal);
        Assert.Contains("Button.agent-service-action:pointerover PathIcon", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"nut-icon-refresh\"", window, StringComparison.Ordinal);

        var actions = window.IndexOf("x:Name=\"ContextualActions\"", StringComparison.Ordinal);
        var details = window.IndexOf("<!-- ================================================ read-only certificate details -->", actions, StringComparison.Ordinal);
        var actionBar = window[actions..details];
        Assert.Contains("Command=\"{Binding ApplyCommand}\"", actionBar, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CancelCommand}\"", actionBar, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(actionBar, "Width=\"116\"").Count);
        Assert.Equal(2, Regex.Matches(actionBar, "Height=\"34\"").Count);
        Assert.DoesNotContain("Strings[Action.Close]", actionBar, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCloseClicked", actionBar, StringComparison.Ordinal);

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
            "src/NutManager.Agent.Config/AgentConfigHost.cs",
            "src/NutManager.Agent.Config/App.axaml.cs",

            // The unified host joins the guard: it is the one place that decides between two
            // privileged modes, and the mode switch must never become a way to run something else.
            "src/NutManager.Agent/Program.cs",
            "src/NutManager.Agent/AgentExecutionMode.cs",
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

public sealed class T41AgentConfigSurfaceTests
{
    /// <summary>
    /// The certificate summary is a surface, not a control.
    ///
    /// It was a ComboBox once, and clicking it dropped the whole machine store over the card. What
    /// replaced it has to stay inert on every action route while remaining hit-testable for its
    /// explanatory tooltip. A test that only asserted the absence of a ComboBox would pass again the
    /// moment somebody attached a Click handler.
    /// </summary>
    [Fact]
    public void TheCertificateSummaryIsAReadonlySurfaceAndNotAPicker()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var start = window.IndexOf("Strings[Https.Certificate]", StringComparison.Ordinal);
        var end = window.IndexOf("x:Name=\"ThumbprintField\"", start, StringComparison.Ordinal);
        var markup = window[start..end];

        Assert.DoesNotContain("<ComboBox", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Popup", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Flyout", markup, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"False\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("IsHitTestVisible=\"False\"", markup, StringComparison.Ordinal);

        // Hover may still explain itself. That is the only thing it does.
        Assert.Contains(
            "ToolTip.Tip=\"{Binding Strings[Https.Certificate.Readonly]}\"", markup, StringComparison.Ordinal);

        // The surface carries no command and no click of its own; the two buttons beside it hold the
        // two actions, and they remain separate from each other.
        var surfaceEnd = markup.IndexOf("<Button", StringComparison.Ordinal);
        Assert.True(surfaceEnd > 0, "The certificate field must be followed by its action buttons.");
        var surface = markup[..surfaceEnd];
        Assert.DoesNotContain("Command=", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=", surface, StringComparison.Ordinal);

        Assert.Contains("Click=\"OnImportCertificateClicked\"", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ToggleCertificateDetailsCommand}\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reset lives in Settings, not on the HTTPS card, and it is still not the old disable button
    /// under a new name: that one, and the status badge beside the title, both stay gone.
    ///
    /// The card was the wrong home for it. The one action that tears down the SSL binding, the URL
    /// reservation and the firewall rule sat directly above the fields it clears, one row from the
    /// checkbox that merely turns the transport off. In Settings it is reached deliberately.
    /// </summary>
    [Fact]
    public void ResetHttpsLivesInSettingsAndNotOnTheHttpsCard()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var card = window.IndexOf("x:Name=\"HttpsEditorCard\"", StringComparison.Ordinal);
        var fields = window.IndexOf("x:Name=\"HttpsEditorFields\"", card, StringComparison.Ordinal);
        var header = window[card..fields];

        Assert.DoesNotContain("ResetHttpsCommand", header, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-reset-https", header, StringComparison.Ordinal);

        // Exactly one reset control in the whole window, and it is the one in Settings.
        Assert.Single(Regex.Matches(window, "Command=\"{Binding ResetHttpsCommand}\""));

        var settings = window.IndexOf("x:Name=\"SettingsResetHttps\"", StringComparison.Ordinal);
        Assert.True(settings > fields, "Reset belongs in the settings surface, after the configuration surface.");

        var reset = window[settings..];
        Assert.Contains("Command=\"{Binding ResetHttpsCommand}\"", reset, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanResetHttps}\"", reset, StringComparison.Ordinal);
        Assert.Contains("Classes=\"agent-reset-https\"", reset, StringComparison.Ordinal);

        // The reason it is unavailable still has to be readable, and a disabled control takes no
        // pointer input, so the tooltip stays on an enabled wrapper - which is why it is looked for
        // across the settings surface rather than inside the button element itself.
        Assert.Contains("ToolTip.Tip=\"{Binding HttpsResetToolTip}\"", window[fields..], StringComparison.Ordinal);

        // Not the filled danger treatment: that belongs to the affirmative button inside the
        // confirmation, where the operator has already been told what will happen.
        Assert.DoesNotContain("nut-danger", window[settings..(settings + 900)], StringComparison.Ordinal);

        Assert.DoesNotContain("Https.Disable", window, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpsStatusText", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// The theme control in Appearance is the desktop application segmented pill, not a switch.
    ///
    /// It was a circular button in the header whose glyph showed the theme it would move to. Correct,
    /// and a control that says only one of the two things it does. This is the control NutManager
    /// already has: a pill holding a sun and a moon, the current one filled.
    ///
    /// The preferences are out of the header entirely now; the gear is what took their place.
    /// </summary>
    [Fact]
    public void TheThemeControlIsTheSegmentedPillInAppearance()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var header = window.IndexOf("x:Name=\"AgentMainHeader\"", StringComparison.Ordinal);
        var surface = window.IndexOf("x:Name=\"ConfigurationSurface\"", header, StringComparison.Ordinal);
        var headerMarkup = window[header..surface];

        Assert.DoesNotContain("agent-theme-toggle", headerMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-language-selector", headerMarkup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsButton\"", headerMarkup, StringComparison.Ordinal);

        var toggle = window.IndexOf("x:Name=\"ThemeToggle\"", StringComparison.Ordinal);
        Assert.True(toggle > surface, "The theme control belongs in the settings surface.");

        // Two halves, each selecting its own theme outright rather than flipping whatever is current.
        var pill = window[toggle..(toggle + 2600)];
        Assert.Contains("Classes=\"agent-theme-toggle\"", pill, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(pill, "Classes=\"agent-theme-option").Count);
        Assert.Contains("Command=\"{Binding SelectLightThemeCommand}\"", pill, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectDarkThemeCommand}\"", pill, StringComparison.Ordinal);

        // The filled half is the theme you are in: the sun is lit while the offer is to go dark.
        Assert.Contains("Classes.selected=\"{Binding ShowDarkThemeAction}\"", pill, StringComparison.Ordinal);
        Assert.Contains("Classes.selected=\"{Binding ShowLightThemeAction}\"", pill, StringComparison.Ordinal);

        // Never a switch for the theme. The one ToggleSwitch left is the startup preference, which is
        // a machine setting with two states rather than a choice between two named things.
        Assert.Single(Regex.Matches(window, "<ToggleSwitch"));
        Assert.Contains("x:Name=\"StartWithWindowsSwitch\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("<RadioButton", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pill is the desktop application one, to the value: same sizes, same brushes, same glyph
    /// movement. Mirrored rather than linked, because NutShellStyles is the desktop shell and depends
    /// on controls this utility does not have - so a test is what keeps the two from drifting.
    /// </summary>
    [Fact]
    public void TheThemePillUsesTheDesktopValues()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var shell = Read("src/NutManager.App/Presentation/Themes/NutShellStyles.axaml");

        foreach (var expected in new[]
        {
            "<Setter Property=\"Width\" Value=\"34\" />",
            "<Setter Property=\"Height\" Value=\"30\" />",
            "<TransformOperationsTransition Property=\"RenderTransform\" Duration=\"0:0:0.34\" Easing=\"CubicEaseOut\" />",
            "rotate(45deg) scale(1.08)",
            "rotate(-18deg) scale(1.06)",
            "NutWarningSoftBrush",
            "NutAccentSoftBrush",
        })
        {
            Assert.Contains(expected, shell, StringComparison.Ordinal);
            Assert.Contains(expected, window, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The gear turns under the pointer, and only when the pointer is on the gear.
    ///
    /// The shared rule is ":pointerover PathIcon.nut-icon-gear" - an ancestor selector with nothing in
    /// front of it, so it matches whenever any ancestor is hovered. In the desktop shell the nearest
    /// hoverable ancestor is a navigation item; here the window itself is one, so pointing anywhere in
    /// the application turned the gear. The values stay shared; only the scope is fixed.
    /// </summary>
    [Fact]
    public void TheSettingsGearTurnsOnlyWhenThePointerIsOnIt()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var styles = Read("src/NutManager.App/Presentation/Themes/NutControlStyles.axaml");

        // The shared definition, and the transition that carries it.
        Assert.Contains("<Style Selector=\"PathIcon.nut-icon-gear\">", styles, StringComparison.Ordinal);
        Assert.Contains("rotate(25deg)", styles, StringComparison.Ordinal);
        Assert.Contains(
            "<TransformOperationsTransition Property=\"RenderTransform\" Duration=\"0:0:0.22\" Easing=\"CubicEaseOut\" />",
            styles,
            StringComparison.Ordinal);

        // The button wears the shared class, and takes the shared angle.
        var gear = window.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal);
        var closing = window.IndexOf("</Button>", gear, StringComparison.Ordinal);
        Assert.Contains("Classes=\"nut-icon-gear\"", window[gear..closing], StringComparison.Ordinal);
        Assert.Contains(
            "<Style Selector=\"Button.agent-settings-button:pointerover PathIcon.nut-icon-gear\">",
            window,
            StringComparison.Ordinal);
        Assert.Contains("rotate(25deg)", window, StringComparison.Ordinal);

        // Hovering the window at large leaves it at rest.
        Assert.Contains(
            "<Style Selector=\"Window:pointerover PathIcon.nut-icon-gear\">",
            window,
            StringComparison.Ordinal);

        // Never a loop: nothing here repeats.
        Assert.DoesNotContain("IterationCount", window, StringComparison.Ordinal);
        Assert.DoesNotContain("RepeatBehavior", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// Status columns say a short state and never render the adapter detail inline. The technical
    /// text reaches the operator through the tooltip instead.
    /// </summary>
    [Fact]
    public void StatusColumnsRenderTheShortStateAndPutTheDetailOnTheTooltip()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var template = window.IndexOf("x:Key=\"AgentResourceItemTemplate\"", StringComparison.Ordinal);
        var end = window.IndexOf("</DataTemplate>", template, StringComparison.Ordinal);
        var markup = window[template..end];

        Assert.Contains("ToolTip.Tip=\"{Binding TooltipText}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Detail}\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("TechnicalDetail", markup, StringComparison.Ordinal);

        // The state glyph leads the state line rather than floating in a column of its own at the
        // far right, where it read as unrelated to the words it was judging.
        Assert.Contains("RowDefinitions=\"Auto,Auto\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions=\"Auto,*,Auto\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three separate certificate actions, in the order they are offered, and the summary surface is
    /// still not a picker.
    /// </summary>
    [Fact]
    public void SelectingImportingAndViewingStayThreeSeparateActions()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var start = window.IndexOf("Strings[Https.Certificate]", StringComparison.Ordinal);
        var end = window.IndexOf("x:Name=\"ThumbprintField\"", start, StringComparison.Ordinal);
        var markup = window[start..end];

        var select = markup.IndexOf("Command=\"{Binding OpenCertificateSelectionCommand}\"", StringComparison.Ordinal);
        var import = markup.IndexOf("Click=\"OnImportCertificateClicked\"", StringComparison.Ordinal);
        var view = markup.IndexOf("Command=\"{Binding ToggleCertificateDetailsCommand}\"", StringComparison.Ordinal);

        Assert.True(select >= 0, "The certificate row must offer selection.");
        Assert.True(import > select, "Import belongs after Select.");
        Assert.True(view > import, "View belongs last.");

        // The surface itself never became a picker again.
        Assert.DoesNotContain("<ComboBox", markup, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"False\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The selection panel is an overlay in this window, reads the catalog, and never sends anybody to
    /// an external console.
    /// </summary>
    [Fact]
    public void TheCertificateSelectionIsAnInternalPanelOverTheMachineStore()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        Assert.Contains("IsVisible=\"{Binding IsSelectingCertificate}\"", window, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding CertificateCandidates}\"", window, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding PendingCertificate}\"", window, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ConfirmCertificateSelectionCommand}\"", window, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CancelCertificateSelectionCommand}\"", window, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanConfirmCertificateSelection}\"", window, StringComparison.Ordinal);

        // The list scrolls; the window still does not.
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Auto\"", window, StringComparison.Ordinal);

        // Never an external console, and never a shell - in the markup, not in the prose. The
        // comments deliberately name certlm.msc to say why the panel exists instead of it, so the
        // scan runs over the file with its comments stripped.
        var withoutComments = System.Text.RegularExpressions.Regex.Replace(
            window, "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (var forbidden in new[] { "certlm", "certmgr", "Process.Start", "powershell", "cmd.exe" })
        {
            Assert.DoesNotContain(forbidden, withoutComments, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The reason Apply is refused has to be readable while Apply is refused.
    ///
    /// A disabled control in Avalonia takes no pointer input, so a tooltip on the button itself would
    /// be invisible in exactly the state that needs it. The tooltip and the accessible help text
    /// belong to an enabled wrapper, and the button inside stays genuinely disabled.
    /// </summary>
    [Fact]
    public void TheReasonApplyIsRefusedIsReachableWhileApplyIsDisabled()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var host = window.IndexOf("x:Name=\"ApplyReasonHost\"", StringComparison.Ordinal);
        Assert.True(host >= 0, "Apply must sit inside a host that can still be pointed at.");

        var end = window.IndexOf("</Border>", host, StringComparison.Ordinal);
        var markup = window[host..end];

        Assert.Contains("ToolTip.Tip=\"{Binding ApplyDisabledReason}\"", markup, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.HelpText=\"{Binding ApplyDisabledReason}\"", markup, StringComparison.Ordinal);

        // The wrapper carries the explanation; the button is still really disabled.
        Assert.Contains("IsEnabled=\"{Binding CanApply}\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled=\"True\"", markup, StringComparison.Ordinal);

        // The tooltip is not on the button, where it could never be shown.
        var button = markup.IndexOf("<Button", StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip.Tip", markup[button..], StringComparison.Ordinal);
    }

    /// <summary>The thumbprint block and its validation row each collapse on their own condition.</summary>
    [Fact]
    public void TheThumbprintAndItsValidationRowCollapseIndependently()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        Assert.Contains("IsVisible=\"{Binding ShowThumbprint}\"", window, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowCertificateFeedback}\"", window, StringComparison.Ordinal);

        // No hacks: the rows collapse, they are not pushed out of sight.
        var card = window.IndexOf("x:Name=\"HttpsEditorCard\"", StringComparison.Ordinal);
        var status = window.IndexOf("x:Name=\"ResourceStatusCard\"", card, StringComparison.Ordinal);
        var markup = window[card..status];
        Assert.DoesNotContain("<Canvas", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ZIndex", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("TranslateTransform", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"0,-", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The apply result lives in the footer, outside the main card entirely.
    ///
    /// It began beside the buttons, where a long refusal was drawn underneath them. Giving it a row
    /// above the action bar stopped that but took the row's height out of the cards, which pushed the
    /// HTTPS card's validation message past its own edge - one overflow traded for another. The
    /// footer is the one strip nothing else competes with, so showing and hiding the message moves
    /// no control above it.
    /// </summary>
    [Fact]
    public void TheApplyResultLivesInTheFooterAndNeverBesideTheButtons()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var footer = window.IndexOf("x:Name=\"AgentFooter\"", StringComparison.Ordinal);
        var mainCard = window.IndexOf("Classes=\"agent-main-card\"", StringComparison.Ordinal);
        var banner = window.IndexOf("x:Name=\"ApplyResultBanner\"", StringComparison.Ordinal);

        Assert.True(banner >= 0, "The window must carry the apply result surface.");
        Assert.True(banner > footer && banner < mainCard,
            "The apply result belongs to the footer, not to the main card.");

        Assert.Contains("IsVisible=\"{Binding HasApplyResult}\"", window, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding ApplyResultDetail}\"", window, StringComparison.Ordinal);

        // The action bar carries buttons and nothing else.
        var actions = window.IndexOf("x:Name=\"ContextualActions\"", StringComparison.Ordinal);
        var actionMarkup = window[actions..];
        Assert.DoesNotContain("Text=\"{Binding ApplyMessage}\"", actionMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyResultBanner", actionMarkup, StringComparison.Ordinal);

        // Apply and Cancel keep the size they were given.
        Assert.Contains("Width=\"116\"", actionMarkup, StringComparison.Ordinal);
        Assert.Contains("Height=\"34\"", actionMarkup, StringComparison.Ordinal);

        // The logs button it replaced only led where Diagnostics already leads.
        Assert.DoesNotContain("Action.ViewLogs", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// The certificate validation row lives inside the HTTPS card, wraps rather than overflowing, and
    /// is not pushed out of sight by a transform.
    /// </summary>
    [Fact]
    public void TheCertificateWarningStaysInsideTheHttpsCard()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var card = window.IndexOf("x:Name=\"HttpsEditorCard\"", StringComparison.Ordinal);
        var status = window.IndexOf("x:Name=\"ResourceStatusCard\"", card, StringComparison.Ordinal);
        var markup = window[card..status];

        var feedback = markup.IndexOf("IsVisible=\"{Binding ShowCertificateFeedback}\"", StringComparison.Ordinal);
        Assert.True(feedback >= 0, "The validation row belongs to the HTTPS card.");

        // It is docked so it reserves its height before the fields take the rest, and it wraps.
        Assert.Contains("DockPanel.Dock=\"Bottom\"", markup, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", markup[feedback..], StringComparison.Ordinal);

        // None of the shortcuts that hide an overflow rather than fixing it.
        Assert.DoesNotContain("<Canvas", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ZIndex", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("TranslateTransform", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"0,-", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The eye answers the pointer with the desktop application's own icon motion, and only the glyph
    /// moves - so the button keeps its measured size and nothing around it shifts.
    /// </summary>
    [Fact]
    public void TheCertificateEyeAnimatesOnHoverAndPress()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var shell = Read("src/NutManager.App/Presentation/Themes/NutControlStyles.axaml");

        Assert.Contains("x:Name=\"ViewCertificateButton\"", window, StringComparison.Ordinal);
        Assert.Contains("Classes=\"agent-eye-glyph\"", window, StringComparison.Ordinal);

        Assert.Contains(
            "Button.agent-eye-button:pointerover PathIcon.agent-eye-glyph", window, StringComparison.Ordinal);
        Assert.Contains(
            "Button.agent-eye-button:pressed PathIcon.agent-eye-glyph", window, StringComparison.Ordinal);

        // The hover scale and the easing are the desktop's, not invented here.
        Assert.Contains("scale(1.14)", shell, StringComparison.Ordinal);
        Assert.Contains("scale(1.14)", window, StringComparison.Ordinal);
        Assert.Contains("scale(0.94)", window, StringComparison.Ordinal);
        Assert.Contains("Easing=\"CubicEaseOut\"", window, StringComparison.Ordinal);

        // Transitions only: nothing loops, so the control costs nothing while nobody points at it.
        var eyeStyle = window.IndexOf("PathIcon.agent-eye-glyph\"", StringComparison.Ordinal);
        var eyeMarkup = window[eyeStyle..(eyeStyle + 1400)];
        Assert.Contains("<TransformOperationsTransition", eyeMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("IterationCount", eyeMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("<Animation", eyeMarkup, StringComparison.Ordinal);

        // And it stays reachable without a pointer.
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding Strings[Https.Certificate.ViewTooltip]}\"",
            window, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transport notice glyph is centred against the whole paragraph, not pinned to its first
    /// line, and it gets there through alignment rather than through an offset.
    /// </summary>
    [Fact]
    public void TheTransportNoticeGlyphIsCentredAgainstItsParagraph()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var strip = window.IndexOf("Classes=\"agent-info-strip\"", StringComparison.Ordinal);
        var end = window.IndexOf("</Border>", strip, StringComparison.Ordinal);
        var markup = window[strip..end];

        Assert.Contains("NutIconInfo", markup, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("VerticalAlignment=\"Top\"", markup, StringComparison.Ordinal);

        // The text still wraps to as many lines as it needs, and is not re-aligned with it.
        Assert.Contains("TextWrapping=\"Wrap\"", markup, StringComparison.Ordinal);

        // Alignment, not an offset.
        Assert.DoesNotContain("TranslateTransform", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<Canvas", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"0,-", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A resource glyph names its row; it never grades it.
    ///
    /// Both lines of a status column carry a glyph, and they answer different questions: the first
    /// says which resource this is and is always green, the second says how that resource is and
    /// takes the semantic brush. Binding the first one to the state would make a foreign binding turn
    /// its own padlock red, and the column would then say the same thing twice in two sizes.
    /// </summary>
    [Fact]
    public void TheResourceGlyphIsGreenIdentityWhileTheStateGlyphKeepsItsSemanticColour()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var template = window.IndexOf("x:Key=\"AgentResourceItemTemplate\"", StringComparison.Ordinal);
        var end = window.IndexOf("</DataTemplate>", template, StringComparison.Ordinal);
        var markup = window[template..end];

        // The resource glyph: a fixed token, never a converter over the state.
        var resourceGlyph = markup.IndexOf("Binding IconKey", StringComparison.Ordinal);
        var resourceEnd = markup.IndexOf("/>", resourceGlyph, StringComparison.Ordinal);
        var resourceMarkup = markup[resourceGlyph..resourceEnd];
        Assert.Contains("Foreground=\"{DynamicResource NutHealthyBrush}\"", resourceMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("StateBrush", resourceMarkup, StringComparison.Ordinal);

        // The state glyph keeps its own colour, from the state.
        var stateGlyph = markup.IndexOf("Binding StateIconKey", StringComparison.Ordinal);
        var stateEnd = markup.IndexOf("/>", stateGlyph, StringComparison.Ordinal);
        Assert.Contains("StateBrush", markup[stateGlyph..stateEnd], StringComparison.Ordinal);

        // A shared token, not a colour invented here.
        Assert.DoesNotContain("Foreground=\"#", markup, StringComparison.Ordinal);

        // One template drives all four columns, so the breathing room between the title and the state
        // is the same in every one of them by construction.
        Assert.Contains("RowSpacing=\"10\"", markup, StringComparison.Ordinal);
    }

    /// <summary>The four resources are still four, and still driven by one template.</summary>
    [Fact]
    public void TheStatusStripStillDrawsFourResourcesFromOneTemplate()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        Assert.Contains("ItemsSource=\"{Binding ResourceStatus}\"", window, StringComparison.Ordinal);
        Assert.Contains("<UniformGrid Columns=\"4\" Rows=\"1\" />", window, StringComparison.Ordinal);

        // Exactly one place declares the item, so the four columns cannot drift apart.
        Assert.Equal(
            1,
            window.Split("x:Key=\"AgentResourceItemTemplate\"", StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// The copy confirmation is an overlay, not a row.
    ///
    /// It has to appear and disappear without moving a card, and it must never intercept a click on
    /// what is underneath it - a confirmation that swallowed a button press would be worse than no
    /// confirmation. It also stays out of the footer and the action bar, which carry persistent
    /// operational information rather than transient feedback.
    /// </summary>
    [Fact]
    public void TheCopyToastIsAnOverlayThatCannotStealAClick()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var toast = window.IndexOf("x:Name=\"CopyToast\"", StringComparison.Ordinal);
        Assert.True(toast >= 0, "The window must carry the copy confirmation surface.");

        var end = window.IndexOf("</Border>", toast, StringComparison.Ordinal);
        var markup = window[toast..end];

        Assert.Contains("IsHitTestVisible=\"False\"", markup, StringComparison.Ordinal);
        Assert.Contains("Classes.visible=\"{Binding IsToastVisible}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ToastMessage}\"", markup, StringComparison.Ordinal);
        Assert.Contains("<PathIcon", markup, StringComparison.Ordinal);

        // Announced without taking focus.
        Assert.Contains("AutomationProperties.LiveSetting", markup, StringComparison.Ordinal);

        // Positioned over the content region, above the buttons - never in the action bar or footer.
        var actions = window.IndexOf("x:Name=\"ContextualActions\"", StringComparison.Ordinal);
        var footer = window.IndexOf("x:Name=\"AgentFooter\"", StringComparison.Ordinal);
        Assert.True(toast < actions, "The toast belongs above the action bar, not inside it.");
        Assert.True(toast > footer, "The toast is not part of the operational footer.");

        // Tokens, never a colour of its own.
        Assert.DoesNotContain("Background=\"#", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"#", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// It fades rather than blinking, and it never loops: a confirmation that kept animating would be
    /// a permanent distraction on a window somebody leaves open.
    /// </summary>
    [Fact]
    public void TheCopyToastFadesAndDoesNotLoop()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var style = window.IndexOf("Border.agent-toast\"", StringComparison.Ordinal);
        Assert.True(style >= 0, "The toast must have a style of its own.");

        var markup = window[style..(style + 1600)];
        Assert.Contains("<DoubleTransition Property=\"Opacity\"", markup, StringComparison.Ordinal);
        Assert.Contains("Easing=\"CubicEaseOut\"", markup, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Opacity\" Value=\"0\" />", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("IterationCount", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<Animation", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The language selector is the desktop application control, showing what the desktop shows:
    /// each language written in its own name.
    ///
    /// It used to be a flyout labelled "PT-BR", which names a culture code rather than a language.
    /// The flyout existed because a list control assigns its selection while it materialises, and
    /// that assignment once overwrote a saved preference - which is handled in the view model now,
    /// so the control can be the ordinary ComboBox the desktop uses.
    /// </summary>
    [Fact]
    public void TheLanguageSelectorIsAComboBoxOfLanguageNames()
    {
        var window = Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var strings = Read("src/NutManager.Agent.Config/Localization/AgentConfigStrings.cs");
        var desktop = Read("src/NutManager.App/Views/SettingsPageView.axaml");

        var surface = window.IndexOf("x:Name=\"ConfigurationSurface\"", StringComparison.Ordinal);
        var selector = window.IndexOf("x:Name=\"LanguageSelector\"", StringComparison.Ordinal);
        Assert.True(selector > surface, "The language selector belongs in the settings surface.");

        var markup = window[selector..(selector + 1200)];
        Assert.Contains("ItemsSource=\"{Binding LanguageOptions}\"", markup, StringComparison.Ordinal);
        Assert.Contains(
            "SelectedItem=\"{Binding SelectedLanguageOption, Mode=TwoWay}\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Title}\"", markup, StringComparison.Ordinal);

        // The same width the desktop gives its own preference dropdowns.
        Assert.Contains("Width=\"200\"", markup, StringComparison.Ordinal);
        Assert.Contains("Width=\"200\"", desktop, StringComparison.Ordinal);

        // The culture code is gone from the window entirely, and the names are autonyms: both
        // cultures spell them the same way, because a language is called what it calls itself.
        Assert.DoesNotContain("SelectedLanguageCode", window, StringComparison.Ordinal);

        // The control itself, not the comment above it: the markup explains at length that "PT-BR"
        // is what this used to show, and that sentence is not the label coming back.
        Assert.DoesNotContain("PT-BR", markup, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(strings, Regex.Escape("Português (Brasil)")).Count);
        Assert.Equal(2, Regex.Matches(strings, Regex.Escape("English (United States)")).Count);

        // One selector, and no radio flyout left behind.
        Assert.Single(Regex.Matches(window, "x:Name=\"LanguageSelector\""));
        Assert.DoesNotContain("<MenuFlyout", window, StringComparison.Ordinal);
        Assert.DoesNotContain("<RadioButton", window, StringComparison.Ordinal);
    }


    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NutManager.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("NutManager.sln was not found.");
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
            HttpsPrefix = "https://nut-server.example.local:5199/",
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
    [InlineData("nut-server.example.local", "nut-server.example.local", true)]
    [InlineData("*.example.local", "nut-server.example.local", true)]
    [InlineData("*.example.local", "node.nut-server.example.local", false)]
    [InlineData("*.example.local", "example.local", false)]
    [InlineData("other.example.local", "nut-server.example.local", false)]
    public void SubjectAlternativeNameMatchingHonorsSingleLabelWildcards(string certificateName, string host, bool expected)
    {
        Assert.Equal(expected, AgentCertificateRules.MatchesHost(Certificate(names: [certificateName]), host));
    }

    /// <summary>
    /// Host matching, in the cases an operator actually meets. Written after a report that a
    /// certificate whose name was on screen was refused for the host that was also on screen.
    ///
    /// The audit found no defect here: the name on screen is the common name, and the common name is
    /// only consulted when the certificate carries no SAN extension. A certificate that has a SAN is
    /// judged by it alone - which is what RFC 6125 requires, and what stops a certificate speaking
    /// for a name it does not actually cover.
    /// </summary>
    [Theory]
    [InlineData("nut-server.example.local", "nut-server.example.local", true)]
    [InlineData("NUT-SERVER.EXAMPLE.LOCAL", "nut-server.example.local", true)]
    [InlineData("nut-server.example.local", "NUT-SERVER.EXAMPLE.LOCAL", true)]
    [InlineData("nut-server.example.local.", "nut-server.example.local", true)]
    [InlineData("nut-server.example.local", "nut-server.example.local.", true)]
    [InlineData("  nut-server.example.local  ", "nut-server.example.local", true)]
    [InlineData("other.example.local", "nut-server.example.local", false)]
    public void SubjectAlternativeNameMatchingIsCaseAndTrailingDotInsensitive(
        string certificateName, string host, bool expected)
    {
        Assert.Equal(expected, AgentCertificateRules.MatchesHost(Certificate(names: [certificateName]), host));
    }

    /// <summary>
    /// A SAN extension is authoritative. A certificate whose common name is the host but whose SAN
    /// names something else does not speak for that host, and accepting it because the name looked
    /// right on screen is the mistake this rule exists to prevent.
    /// </summary>
    [Fact]
    public void ASubjectAlternativeNameOverridesAMatchingCommonName()
    {
        var certificate = Certificate(
            subject: "CN=nut-server.example.local, O=Example, C=BR",
            names: ["other.example.local"]);

        Assert.False(AgentCertificateRules.MatchesHost(certificate, "nut-server.example.local"));
        Assert.True(AgentCertificateRules.MatchesHost(certificate, "other.example.local"));
    }

    /// <summary>
    /// The common name is the fallback, and it is read out of a realistic distinguished name rather
    /// than assumed to be the whole subject.
    /// </summary>
    [Theory]
    [InlineData("CN=nut-server.example.local", true)]
    [InlineData("CN=nut-server.example.local, O=Example, C=BR", true)]
    [InlineData("CN=NUT-SERVER.EXAMPLE.LOCAL, O=Example", true)]
    [InlineData("O=Example, CN=nut-server.example.local, C=BR", true)]
    [InlineData("CN=EXAMPLE-CA, DC=example, DC=local", false)]
    [InlineData("O=Example, C=BR", false)]
    public void TheCommonNameIsUsedOnlyWhenNoSubjectAlternativeNameExists(string subject, bool expected)
    {
        var certificate = Certificate(subject: subject, names: []);

        Assert.Equal(expected, AgentCertificateRules.MatchesHost(certificate, "nut-server.example.local"));
    }

    /// <summary>Nothing matches an empty host: a blank draft never earns a certificate.</summary>
    [Fact]
    public void AnEmptyHostNeverMatches()
    {
        var certificate = Certificate(names: ["nut-server.example.local"]);

        Assert.False(AgentCertificateRules.MatchesHost(certificate, string.Empty));
        Assert.False(AgentCertificateRules.MatchesHost(certificate, "   "));
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
        var resolution = new AgentIdentityResolution(true, "principal", "S-1-5-21-1000", kind, "EXAMPLE", null);

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

    private const string Host = "nut-server.example.local";
    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static AgentCertificateSummary Certificate(
        bool hasPrivateKey = true,
        bool supportsServerAuthentication = true,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        IReadOnlyList<string>? names = null,
        string? subject = null) =>
        new(
            Thumbprint,
            subject ?? $"CN={Host}",
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

public sealed class T41AgentConfigUiPreferencesTests
{
    /// <summary>
    /// The two preferences share one file and must not overwrite each other.
    ///
    /// They are set from different controls at different moments, so a write that serialised only
    /// the field it knew about would drop the other one every time.
    /// </summary>
    [Fact]
    public void ThemeAndLanguageShareTheFileWithoutOverwritingEachOther()
    {
        WithTemporaryPreferences((preferences, path) =>
        {
            preferences.WriteLanguage(UiLanguagePreference.EnUs);
            preferences.WriteTheme(ThemePreference.Dark);

            Assert.Equal(UiLanguagePreference.EnUs, preferences.ReadLanguage());
            Assert.Equal(ThemePreference.Dark, preferences.ReadTheme());

            preferences.WriteLanguage(UiLanguagePreference.PtBr);

            Assert.Equal(ThemePreference.Dark, preferences.ReadTheme());
            Assert.Equal(UiLanguagePreference.PtBr, preferences.ReadLanguage());

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("pt-BR", json.RootElement.GetProperty("language").GetString());
            Assert.Equal("dark", json.RootElement.GetProperty("theme").GetString());
        });
    }

    [Theory]
    [InlineData(ThemePreference.Light, "light")]
    [InlineData(ThemePreference.Dark, "dark")]
    public void EachChosenThemeIsStoredAsAStableTag(ThemePreference theme, string expected)
    {
        WithTemporaryPreferences((preferences, path) =>
        {
            preferences.WriteTheme(theme);

            Assert.Equal(theme, preferences.ReadTheme());

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(expected, json.RootElement.GetProperty("theme").GetString());
        });
    }

    /// <summary>
    /// A missing file, an unreadable one and a tag from some later version all mean the same thing:
    /// nobody has chosen. None of them may throw, because this is a convenience and it must never be
    /// the reason an administration utility refuses to open.
    /// </summary>
    [Fact]
    public void AnUnreadableOrUnknownPreferenceReadsAsNoChoiceRatherThanThrowing()
    {
        WithTemporaryPreferences((preferences, path) =>
        {
            Assert.Null(preferences.ReadTheme());
            Assert.Null(preferences.ReadLanguage());

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");
            Assert.Null(preferences.ReadTheme());

            File.WriteAllText(path, "{\"language\":\"pt-BR\",\"theme\":\"solarized\"}");
            Assert.Null(preferences.ReadTheme());

            // The unknown theme did not take the language down with it.
            Assert.Equal(UiLanguagePreference.PtBr, preferences.ReadLanguage());
        });
    }

    private static void WithTemporaryPreferences(Action<AgentConfigUiPreferences, string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NutManager-T41-Ui-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "agent-config-ui.json");

        try
        {
            body(new AgentConfigUiPreferences(path), path);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LanguagePreferenceRoundTripsAsTheOnlyStoredUiValue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NutManager-T41-Ui-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "agent-config-ui.json");

        try
        {
            var preferences = new AgentConfigUiPreferences(path);

            preferences.WriteLanguage(UiLanguagePreference.EnUs);

            Assert.Equal(UiLanguagePreference.EnUs, preferences.ReadLanguage());
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var property = Assert.Single(json.RootElement.EnumerateObject());
            Assert.Equal("language", property.Name);
            Assert.Equal("en-US", property.Value.GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"language\":\"fr-FR\"}")]
    public void CorruptOrUnsupportedPreferenceFallsBackWithoutFailing(string contents)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NutManager-T41-Ui-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "agent-config-ui.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, contents);

            Assert.Null(new AgentConfigUiPreferences(path).ReadLanguage());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingPreferenceFallsBackWithoutCreatingAFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NutManager-T41-Ui-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "agent-config-ui.json");

        var preferences = new AgentConfigUiPreferences(path);

        Assert.Null(preferences.ReadLanguage());
        Assert.False(File.Exists(path));
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
        context.ViewModel.HttpsHost = "wrong.example.local";

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
        Assert.Equal(AgentApplyResultKind.Error, context.ViewModel.ApplyResultKind);

        // The banner says something an operator can read; the adapter sentence is kept whole on the
        // tooltip rather than being the thing drawn beside the buttons.
        Assert.Equal("The HTTPS configuration could not be applied.", context.ViewModel.ApplyMessage);
        Assert.Equal("binding failed", context.ViewModel.ApplyResultDetail);
        Assert.NotEqual(context.ViewModel.ApplyMessage, context.ViewModel.ApplyResultDetail);
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
        Assert.Equal("https://nut-server.example.local:5199/", written.HttpsPrefix);
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
            AgentMembershipOutcome.AlreadyMember, @"EXAMPLE\operator");
        await context.ViewModel.RefreshAsync();
        context.ViewModel.NewMemberAccount = @"EXAMPLE\operator";

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

    // ================================================================ reset HTTPS

    /// <summary>
    /// Reset asks first. The button opens a confirmation and does nothing else: no resource is
    /// touched, no file is written, and the endpoint on screen is still the endpoint that was there.
    /// </summary>
    [Fact]
    public async Task ResettingHttpsAsksBeforeItRemovesAnything()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ResetHttpsCommand.Execute(null);

        Assert.Equal(AgentConfigConfirmation.ResetHttps, context.ViewModel.PendingConfirmation);
        Assert.Empty(context.Resources.RemoveRequests);
        Assert.Empty(context.Store.Writes);
        Assert.True(context.ViewModel.HttpsEnabled);
        Assert.Equal(new Uri(HttpsDocument().HttpsPrefix!).Host, context.ViewModel.HttpsHost);
    }

    /// <summary>Cancelling is a full stop: the machine and the configuration are exactly as they were.</summary>
    [Fact]
    public async Task CancellingTheResetLeavesTheMachineAndTheConfigurationAlone()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ResetHttpsCommand.Execute(null);
        context.ViewModel.CancelConfirmationCommand.Execute(null);

        Assert.Equal(AgentConfigConfirmation.None, context.ViewModel.PendingConfirmation);
        Assert.Empty(context.Resources.RemoveRequests);
        Assert.Empty(context.Store.Writes);
        Assert.True(context.ViewModel.HttpsEnabled);
        Assert.NotNull(context.ViewModel.SelectedCertificate);
    }

    /// <summary>
    /// A confirmed reset clears the endpoint this product configured and returns the port to its
    /// default, while the certificate stays in the machine store. Removing a certificate is not what
    /// resetting a listener means, and the catalog is the proof.
    /// </summary>
    [Fact]
    public async Task ResettingHttpsClearsTheEndpointAndLeavesTheCertificateInstalled()
    {
        var context = CreateContext(document: HttpsDocument());
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager));
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ResetHttpsCommand.Execute(null);
        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.HttpsEnabled);
        Assert.Equal(string.Empty, context.ViewModel.HttpsHost);
        Assert.Equal(5199, context.ViewModel.HttpsPort);
        Assert.Null(context.ViewModel.SelectedCertificate);
        Assert.Null(context.ViewModel.CertificateThumbprint);

        var written = Assert.Single(context.Store.Writes);
        Assert.False(written.HttpsEnabled);
        Assert.Null(written.HttpsPrefix);
        Assert.Null(written.CertificateThumbprint);

        // The certificate is still in the store. This is the confirmation's central promise, and it
        // is asserted rather than trusted.
        Assert.NotEmpty(context.Certificates.List());
        Assert.False(context.ViewModel.ApplyFailed);
    }

    /// <summary>
    /// The reset asks for everything and the ownership rule decides what that means. Foreign and
    /// unknown resources survive because the adapter refuses them, not because the caller guessed.
    /// </summary>
    [Fact]
    public async Task ResettingHttpsDefersToTheOwnershipRuleForWhatMayGo()
    {
        var context = CreateContext(document: HttpsDocument());
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.ForeignOwner),
            new AgentResourceState(AgentResourceOwnership.Unknown));
        context.Resources.RemoveResult = AgentHttpsResourceResult.Success(
            ["ssl binding"],
            ["The URL reservation belongs to another product.", "The firewall rule owner is unknown."]);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ResetHttpsCommand.Execute(null);
        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);

        var request = Assert.Single(context.Resources.RemoveRequests);
        Assert.True(request.RemoveSslBinding);
        Assert.True(request.RemoveUrlReservation);
        Assert.True(request.RemoveFirewallRule);

        // What the adapter refused is reported, not retried and not hidden.
        Assert.Contains("another product", context.ViewModel.ApplyResultDetail);
        Assert.Contains("owner is unknown", context.ViewModel.ApplyResultDetail);
        Assert.False(context.ViewModel.ApplyFailed);
        Assert.Equal(AgentApplyResultKind.Success, context.ViewModel.ApplyResultKind);
    }

    /// <summary>
    /// A failed removal stops before the file. Writing "HTTPS is off" while a binding is still live
    /// would leave the configuration describing a machine that does not exist.
    /// </summary>
    [Fact]
    public async Task AFailedRemovalLeavesTheConfigurationUnwritten()
    {
        var context = CreateContext(document: HttpsDocument());
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager));
        context.Resources.RemoveResult = AgentHttpsResourceResult.Failed(
            "The SSL binding could not be removed.", ["url reservation"]);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ResetHttpsCommand.Execute(null);
        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.True(context.ViewModel.ApplyFailed);
        Assert.Empty(context.Store.Writes);
        Assert.True(context.ViewModel.HttpsEnabled);
        Assert.Contains("could not be removed", context.ViewModel.ApplyResultDetail);

        // What did come off before the failure is named, so the operator knows the machine is now
        // between two states rather than in the one it started from.
        Assert.Contains("url reservation", context.ViewModel.ApplyResultDetail);
    }

    /// <summary>
    /// The last transport rule outranks the reset. HTTPS alone cannot be reset away, and the refusal
    /// names what to do about it rather than turning the named pipe on to make room.
    /// </summary>
    [Fact]
    public async Task ResettingHttpsIsRefusedWhileItIsTheOnlyTransport()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();
        context.ViewModel.NamedPipeEnabled = false;

        Assert.False(context.ViewModel.CanResetHttps);
        Assert.NotNull(context.ViewModel.HttpsResetBlockedReason);
        Assert.Contains("SMB", context.ViewModel.HttpsResetBlockedReason);

        context.ViewModel.ResetHttpsCommand.Execute(null);

        Assert.Equal(AgentConfigConfirmation.None, context.ViewModel.PendingConfirmation);
        Assert.Empty(context.Resources.RemoveRequests);
        Assert.Empty(context.Store.Writes);

        // Nothing turned the named pipe on to satisfy the rule on the operator's behalf.
        Assert.False(context.ViewModel.NamedPipeEnabled);
        Assert.True(context.ViewModel.HttpsEnabled);
    }

    [Fact]
    public async Task ResetAvailabilityUpdatesImmediatelyWhenTheOtherTransportChanges()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();

        context.ViewModel.NamedPipeEnabled = false;

        Assert.False(context.ViewModel.CanResetHttps);
        Assert.Equal(context.ViewModel.HttpsResetBlockedReason, context.ViewModel.HttpsResetToolTip);

        context.ViewModel.NamedPipeEnabled = true;

        Assert.True(context.ViewModel.CanResetHttps);
        Assert.Null(context.ViewModel.HttpsResetBlockedReason);
        Assert.Equal(context.ViewModel.Strings["Https.Reset.Tooltip"], context.ViewModel.HttpsResetToolTip);
    }

    /// <summary>A reset never starts the agent, and a running one is asked rather than restarted.</summary>
    [Fact]
    public async Task ResettingHttpsNeverRestartsTheServiceOnItsOwn()
    {
        var context = CreateContext(document: HttpsDocument(), serviceState: AgentServiceState.Running);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ResetHttpsCommand.Execute(null);
        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(AgentConfigConfirmation.RestartService, context.ViewModel.PendingConfirmation);
    }

    // ================================================================ choosing an installed certificate

    /// <summary>
    /// The panel offers what the machine already holds, and nothing else happens on the way there.
    ///
    /// This is the whole point of the feature: a certificate already in the store should be selectable
    /// without the operator finding the original file and importing a second copy of it.
    /// </summary>
    [Fact]
    public async Task OpeningTheSelectionListsInstalledCertificatesWithoutImportingAnything()
    {
        var context = CreateContext();
        context.Certificates.Add(Certificate("11111111111111111111111111111111111111AA", "CN=nut-server.example.local"));
        context.Certificates.Add(Certificate("22222222222222222222222222222222222222BB", "CN=backup.example.local"));
        await context.ViewModel.RefreshAsync();

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);

        Assert.True(context.ViewModel.IsSelectingCertificate);
        Assert.True(context.ViewModel.HasCertificateCandidates);
        Assert.Equal(3, context.ViewModel.CertificateCandidates.Count);

        // Reading the store is all that happened.
        Assert.Equal(0, context.Importer.ImportCalls);
        Assert.Empty(context.Store.Writes);
        Assert.Empty(context.Resources.RemoveRequests);
        Assert.Equal(0, context.Resources.ApplyCalls);
    }

    /// <summary>
    /// Several certificates can share a common name and an issuer, differing only in dates and
    /// thumbprint. The list has to let an operator tell those apart before confirming, so each row
    /// carries an abbreviated thumbprint and they are all distinct.
    /// </summary>
    [Fact]
    public async Task CertificatesSharingANameStayDistinguishable()
    {
        const string Subject = "CN=shared.example.local";

        var context = CreateContext();
        context.Certificates.Add(Certificate("AAAAAAAA11111111111111111111111111111111", Subject));
        context.Certificates.Add(Certificate("BBBBBBBB22222222222222222222222222222222", Subject));
        await context.ViewModel.RefreshAsync();

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);

        var sameName = context.ViewModel.CertificateCandidates
            .Where(candidate => string.Equals(candidate.Subject, Subject, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, sameName.Length);
        Assert.Equal(2, sameName.Select(candidate => candidate.ShortThumbprint).Distinct(StringComparer.Ordinal).Count());

        // Nothing is chosen for the operator when the draft has no certificate yet: picking one of two
        // look-alikes on their behalf is how the wrong one ends up configured.
        Assert.Null(context.ViewModel.PendingCertificate);
        Assert.False(context.ViewModel.CanConfirmCertificateSelection);
    }

    /// <summary>
    /// Confirming changes the draft and only the draft. The file, the binding, the firewall rule and
    /// the service all still wait for Apply.
    /// </summary>
    [Fact]
    public async Task ConfirmingASelectionChangesTheDraftAndNothingElse()
    {
        const string Chosen = "CCCCCCCC33333333333333333333333333333333";

        var context = CreateContext();
        context.Certificates.Add(Certificate(Chosen, "CN=nut-server.example.local"));
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);
        context.ViewModel.PendingCertificate = context.ViewModel.CertificateCandidates
            .Single(candidate => candidate.Thumbprint == Chosen);
        context.ViewModel.ConfirmCertificateSelectionCommand.Execute(null);

        Assert.False(context.ViewModel.IsSelectingCertificate);
        Assert.Equal(Chosen, context.ViewModel.CertificateThumbprint);
        Assert.True(context.ViewModel.IsDirty);

        Assert.Empty(context.Store.Writes);
        Assert.Equal(0, context.Importer.ImportCalls);
        Assert.Equal(0, context.Resources.ApplyCalls);
        Assert.Empty(context.Resources.RemoveRequests);
    }

    /// <summary>Cancelling is a full stop: the previous certificate stays, and so does the dirty state.</summary>
    [Fact]
    public async Task CancellingTheSelectionLeavesTheDraftAlone()
    {
        const string Other = "DDDDDDDD44444444444444444444444444444444";

        var context = CreateContext(document: HttpsDocument());
        context.Certificates.Add(Certificate(Other, "CN=other.example.local"));
        await context.ViewModel.RefreshAsync();

        var before = context.ViewModel.CertificateThumbprint;
        var dirtyBefore = context.ViewModel.IsDirty;

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);
        context.ViewModel.PendingCertificate = context.ViewModel.CertificateCandidates
            .Single(candidate => candidate.Thumbprint == Other);
        context.ViewModel.CancelCertificateSelectionCommand.Execute(null);

        Assert.False(context.ViewModel.IsSelectingCertificate);
        Assert.Equal(before, context.ViewModel.CertificateThumbprint);
        Assert.Equal(dirtyBefore, context.ViewModel.IsDirty);
        Assert.Empty(context.Store.Writes);
    }

    /// <summary>
    /// The list is ordered so a usable certificate leads, and never filtered: an operator diagnosing a
    /// refused endpoint needs to see the one that is failing, not have it hidden for failing.
    /// </summary>
    [Fact]
    public async Task UsableCertificatesLeadTheListAndUnusableOnesStayVisible()
    {
        const string Keyless = "EEEEEEEE55555555555555555555555555555555";

        var context = CreateContext();
        context.Certificates.Add(Certificate(Keyless, "CN=nut-server.example.local", hasPrivateKey: false));
        context.Certificates.Add(Certificate("FFFFFFFF66666666666666666666666666666666", "CN=nut-server.example.local"));
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);

        Assert.True(context.ViewModel.CertificateCandidates[0].IsUsable);
        Assert.Contains(context.ViewModel.CertificateCandidates, candidate => !candidate.IsUsable);

        var keyless = context.ViewModel.CertificateCandidates.Single(candidate => candidate.Thumbprint == Keyless);
        Assert.False(keyless.IsUsable);
        Assert.False(keyless.HasPrivateKey);
    }

    /// <summary>
    /// The main scenario end to end: a certificate already installed becomes the one the agent will
    /// use, and Apply unblocks because of it.
    /// </summary>
    [Fact]
    public async Task SelectingAnInstalledCertificateUnblocksApply()
    {
        const string Installed = "0123456789ABCDEF0123456789ABCDEF01234567";

        var context = CreateContext();
        context.Certificates.Add(Certificate(Installed, "CN=nut-server.example.local"));
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";
        context.ViewModel.HttpsPort = 5199;

        Assert.False(context.ViewModel.CanApply);
        Assert.Equal("Select a valid certificate to enable HTTPS.", context.ViewModel.ApplyDisabledReason);

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);
        context.ViewModel.PendingCertificate = context.ViewModel.CertificateCandidates
            .Single(candidate => candidate.Thumbprint == Installed);
        context.ViewModel.ConfirmCertificateSelectionCommand.Execute(null);

        Assert.True(context.ViewModel.CanApply);
        Assert.Null(context.ViewModel.ApplyDisabledReason);
        Assert.Equal(0, context.Importer.ImportCalls);
    }

    /// <summary>An unusable certificate can be chosen, and Apply stays shut with the real reason.</summary>
    [Fact]
    public async Task SelectingAnUnusableCertificateKeepsApplyBlockedWithItsRealReason()
    {
        const string Keyless = "99999999AAAAAAAA99999999AAAAAAAA99999999";

        var context = CreateContext();
        context.Certificates.Add(Certificate(Keyless, "CN=nut-server.example.local", hasPrivateKey: false));
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);
        context.ViewModel.PendingCertificate = context.ViewModel.CertificateCandidates
            .Single(candidate => candidate.Thumbprint == Keyless);
        context.ViewModel.ConfirmCertificateSelectionCommand.Execute(null);

        Assert.False(context.ViewModel.CanApply);

        // The reason is the problem this certificate actually has, not the generic "choose one" line.
        Assert.Contains("private key", context.ViewModel.ApplyDisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A saved thumbprint whose certificate is gone must not crash, and stays recoverable.</summary>
    [Fact]
    public async Task AConfiguredThumbprintMissingFromTheStoreLeavesTheWindowUsable()
    {
        var document = new AgentTransportConfigurationDocument
        {
            NamedPipeEnabled = true,
            HttpsEnabled = true,
            HttpsPrefix = "https://nut-server.example.local:5199/",
            CertificateThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567",
        };

        var context = CreateContext(document: document, withCertificate: false);
        await context.ViewModel.RefreshAsync();

        Assert.Null(context.ViewModel.SelectedCertificate);
        Assert.False(context.ViewModel.CanApply);

        // And the way out is available.
        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);
        Assert.True(context.ViewModel.IsSelectingCertificate);
        Assert.Null(context.ViewModel.PendingCertificate);
    }

    /// <summary>An empty store says so, rather than showing an empty box.</summary>
    [Fact]
    public async Task AnEmptyStoreIsReportedAsSuch()
    {
        var context = CreateContext(withCertificate: false);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);

        Assert.True(context.ViewModel.IsSelectingCertificate);
        Assert.False(context.ViewModel.HasCertificateCandidates);
        Assert.Empty(context.ViewModel.CertificateCandidates);
    }

    // ================================================================ why Apply is refused

    /// <summary>
    /// Each refusal names its own cause, in the order the conditions are actually evaluated, so the
    /// reason shown is the one that would still be blocking after the operator fixes it.
    /// </summary>
    [Fact]
    public async Task ApplyExplainsExactlyWhyItIsRefused()
    {
        var context = CreateContext(withCertificate: false);
        await context.ViewModel.RefreshAsync();

        Assert.Equal("No pending changes.", context.ViewModel.ApplyDisabledReason);

        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = string.Empty;
        Assert.Equal("Enter a valid host or FQDN for HTTPS.", context.ViewModel.ApplyDisabledReason);

        context.ViewModel.HttpsHost = "nut-server.example.local";
        context.ViewModel.HttpsPort = 0;
        Assert.Equal("Enter a port between 1 and 65535.", context.ViewModel.ApplyDisabledReason);

        context.ViewModel.HttpsPort = 5199;
        Assert.Equal("Select a valid certificate to enable HTTPS.", context.ViewModel.ApplyDisabledReason);
    }

    /// <summary>
    /// A certificate is only required by the transport that needs one. With HTTPS off, its absence
    /// must not block a perfectly valid named-pipe configuration.
    /// </summary>
    [Fact]
    public async Task WithHttpsOffAMissingCertificateDoesNotBlockApply()
    {
        var context = CreateContext(document: HttpsDocument(), withCertificate: false);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.HttpsEnabled = false;

        Assert.True(context.ViewModel.CanApply);
        Assert.Null(context.ViewModel.ApplyDisabledReason);
    }

    /// <summary>The reason follows the language, like everything else on the window.</summary>
    [Fact]
    public async Task TheApplyReasonIsLocalised()
    {
        var context = CreateContext(withCertificate: false);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";

        Assert.Equal("Select a valid certificate to enable HTTPS.", context.ViewModel.ApplyDisabledReason);

        context.ViewModel.SelectedLanguage = UiLanguagePreference.PtBr;

        Assert.Equal(
            "Selecione um certificado válido para habilitar HTTPS.", context.ViewModel.ApplyDisabledReason);
    }

    /// <summary>
    /// With no certificate the surface already says so, and the warning row below the thumbprint must
    /// not repeat it. The thumbprint block collapses with it.
    /// </summary>
    [Fact]
    public async Task WithNoCertificateTheWarningIsNotRepeatedBelowTheThumbprint()
    {
        var context = CreateContext(withCertificate: false);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";

        Assert.False(context.ViewModel.HasSelectedCertificate);
        Assert.False(context.ViewModel.ShowThumbprint);
        Assert.False(context.ViewModel.ShowCertificateFeedback);
    }

    /// <summary>A chosen certificate that is unusable is exactly the case the row exists for.</summary>
    [Fact]
    public async Task AnUnusableSelectedCertificateStillShowsItsValidationRow()
    {
        const string Keyless = "0123456789ABCDEF0123456789ABCDEF01234567";

        var context = CreateContext(withCertificate: false);
        context.Certificates.Add(Certificate(Keyless, "CN=nut-server.example.local", hasPrivateKey: false));
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";

        context.ViewModel.OpenCertificateSelectionCommand.Execute(null);
        context.ViewModel.PendingCertificate = context.ViewModel.CertificateCandidates
            .Single(candidate => candidate.Thumbprint == Keyless);
        context.ViewModel.ConfirmCertificateSelectionCommand.Execute(null);

        Assert.True(context.ViewModel.ShowThumbprint);
        Assert.True(context.ViewModel.ShowCertificateFeedback);
        Assert.Contains("private key", context.ViewModel.CertificateFeedbackMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A valid certificate is confirmed rather than passed over in silence.
    ///
    /// Saying nothing at the moment an operator wants reassurance reads as "not checked yet", which
    /// is the one thing this line exists to rule out. Green, and in the same row a problem would use.
    /// </summary>
    [Fact]
    public async Task AValidSelectedCertificateIsConfirmedInGreen()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);

        Assert.True(context.ViewModel.ShowThumbprint);
        Assert.True(context.ViewModel.ShowCertificateFeedback);
        Assert.Equal("healthy", context.ViewModel.CertificateFeedbackStateClass);
        Assert.Equal("AgentIconStateReady", context.ViewModel.CertificateFeedbackIconKey);
        Assert.Equal(
            "Certificate is valid and matches the host.", context.ViewModel.CertificateFeedbackMessage);
    }

    private static AgentCertificateSummary Certificate(
        string thumbprint, string subject, bool hasPrivateKey = true) =>
        new(
            thumbprint,
            subject,
            "CN=EXAMPLE-CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            HasPrivateKey: hasPrivateKey,
            SupportsServerAuthentication: true,
            SubjectAlternativeNames: [subject.Replace("CN=", string.Empty, StringComparison.Ordinal)]);

    // ================================================================ the apply banner

    /// <summary>With nothing attempted there is no banner, so the action bar keeps its own layout.</summary>
    [Fact]
    public async Task NoAttemptMeansNoBanner()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        Assert.Equal(AgentApplyResultKind.None, context.ViewModel.ApplyResultKind);
        Assert.False(context.ViewModel.HasApplyResult);
        Assert.Null(context.ViewModel.ApplyMessage);
        Assert.Null(context.ViewModel.ApplyResultDetail);
    }

    [Fact]
    public async Task ASavedConfigurationReportsSuccess()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(AgentApplyResultKind.Success, context.ViewModel.ApplyResultKind);
        Assert.True(context.ViewModel.HasApplyResult);
        Assert.False(context.ViewModel.ApplyFailed);
        Assert.Equal("Configuration saved.", context.ViewModel.ApplyMessage);
    }

    /// <summary>
    /// The refusal an operator actually meets: another application is already bound to the port.
    ///
    /// The short line is localised and derived from the ownership this window already queried, not
    /// from matching English words in the adapter sentence. That sentence is kept, whole, as the
    /// detail - it names the AppId, and discarding it to make the banner short would trade one bad
    /// outcome for another.
    /// </summary>
    [Fact]
    public async Task AForeignSslBindingIsReportedInOneLocalisedLineWithTheDetailKept()
    {
        const string Adapter =
            "An SSL certificate is already bound to port 5199 by another application. " +
            "Choose a different port, or remove that binding with the tool that created it.";

        var context = CreateContext();
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.ForeignOwner, "AppId {00000000-0000-0000-0000-000000000001}"),
            AgentResourceState.Absent,
            AgentResourceState.Absent);
        context.Resources.ApplyResult = AgentHttpsResourceResult.Failed(Adapter);
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(AgentApplyResultKind.Error, context.ViewModel.ApplyResultKind);
        Assert.Equal("Port 5199 already has an SSL certificate binding.", context.ViewModel.ApplyMessage);
        Assert.Equal(Adapter, context.ViewModel.ApplyResultDetail);
        Assert.NotEqual(context.ViewModel.ApplyMessage, context.ViewModel.ApplyResultDetail);
        Assert.Empty(context.Store.Writes);
    }

    [Fact]
    public async Task TheForeignBindingLineIsLocalised()
    {
        var context = CreateContext(language: UiLanguagePreference.PtBr);
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.ForeignOwner),
            AgentResourceState.Absent,
            AgentResourceState.Absent);
        context.Resources.ApplyResult = AgentHttpsResourceResult.Failed("An SSL certificate is already bound...");
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("A porta 5199 já possui um certificado SSL vinculado.", context.ViewModel.ApplyMessage);
        Assert.DoesNotContain("SSL certificate is already", context.ViewModel.ApplyMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure with no recognised cause gets the honest generic line rather than an invented one,
    /// and the platform text still travels with it.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedFailureGetsTheGenericLineAndKeepsItsDetail()
    {
        var context = CreateContext();
        context.Resources.ApplyResult = AgentHttpsResourceResult.Failed("Something nobody mapped (0x80004005).");
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);

        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("The HTTPS configuration could not be applied.", context.ViewModel.ApplyMessage);
        Assert.Equal("Something nobody mapped (0x80004005).", context.ViewModel.ApplyResultDetail);
    }

    /// <summary>
    /// The banner and the disabled reason answer different questions and must not merge: one says why
    /// the button cannot be pressed, the other what happened when it was.
    /// </summary>
    [Fact]
    public async Task TheApplyBannerAndTheDisabledReasonStaySeparate()
    {
        var context = CreateContext(withCertificate: false);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";

        Assert.NotNull(context.ViewModel.ApplyDisabledReason);
        Assert.False(context.ViewModel.HasApplyResult);
    }

    // ================================================================ status honesty

    /// <summary>
    /// The strip must not claim a resource is absent before it has looked.
    ///
    /// While the draft is incomplete there is no endpoint to query for, and the strip used to report
    /// every resource as absent anyway. That is how a screen came to say the SSL binding was not
    /// configured on a machine where Apply, which does query, found another application already bound
    /// to the port.
    /// </summary>
    [Fact]
    public async Task TheStatusStripDoesNotClaimAResourceIsAbsentBeforeItHasLooked()
    {
        var context = CreateContext(withCertificate: false);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;

        Assert.Equal(4, context.ViewModel.ResourceStatus.Count);
        Assert.All(context.ViewModel.ResourceStatus, item =>
        {
            Assert.Equal("Not checked", item.Detail);
            Assert.NotEqual("Not configured", item.Detail);
            Assert.Contains("only queried", item.TechnicalDetail, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Completing the draft makes the window ask, so the strip stops saying "not checked" and starts
    /// reporting what the machine actually holds - including a binding that is not ours.
    /// </summary>
    [Fact]
    public async Task CompletingTheDraftMakesTheStripReportTheMachine()
    {
        var context = CreateContext();
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.ForeignOwner, "AppId {00000000-0000-0000-0000-000000000001}"),
            AgentResourceState.Absent,
            AgentResourceState.Absent);
        await context.ViewModel.RefreshAsync();

        EnableValidHttps(context.ViewModel);

        Assert.Equal("Owned by another application", context.ViewModel.ResourceStatus[0].Detail);
        Assert.Equal(AgentDiagnosticState.Attention, context.ViewModel.ResourceStatus[0].State);
        Assert.Contains("AppId", context.ViewModel.ResourceStatus[0].TechnicalDetail);
    }

    // ================================================================ copy feedback

    [Fact]
    public async Task NothingIsAnnouncedBeforeAnythingIsCopied()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        Assert.False(context.ViewModel.IsToastVisible);
        Assert.Null(context.ViewModel.ToastMessage);
    }

    [Fact]
    public async Task ASuccessfulCopyIsConfirmed()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ReportEndpointCopy(succeeded: true);

        Assert.True(context.ViewModel.IsToastVisible);
        Assert.Equal(AgentToastKind.Success, context.ViewModel.ToastKind);
        Assert.Equal("Copied!", context.ViewModel.ToastMessage);
    }

    /// <summary>
    /// A clipboard the platform refused says so. Announcing a copy that did not happen is worse than
    /// announcing nothing, because the operator then pastes whatever was there before.
    /// </summary>
    [Fact]
    public async Task AFailedCopyIsNotReportedAsSuccess()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ReportEndpointCopy(succeeded: false);

        Assert.True(context.ViewModel.IsToastVisible);
        Assert.Equal(AgentToastKind.Error, context.ViewModel.ToastKind);
        Assert.Equal("Could not copy.", context.ViewModel.ToastMessage);
    }

    [Fact]
    public async Task TheCopyConfirmationIsLocalised()
    {
        var context = CreateContext(language: UiLanguagePreference.PtBr);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ReportEndpointCopy(succeeded: true);
        Assert.Equal("Copiado!", context.ViewModel.ToastMessage);

        context.ViewModel.ReportEndpointCopy(succeeded: false);
        Assert.Equal("Não foi possível copiar.", context.ViewModel.ToastMessage);
    }

    /// <summary>
    /// Copying is a clipboard operation and nothing else. It must not make the configuration dirty,
    /// enable Apply, change the endpoint, write the file or reach the service.
    /// </summary>
    [Fact]
    public async Task CopyingTouchesNothingBelongingToTheAgent()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();

        var dirtyBefore = context.ViewModel.IsDirty;
        var canApplyBefore = context.ViewModel.CanApply;
        var endpointBefore = context.ViewModel.HttpsEndpoint;
        var hostBefore = context.ViewModel.HttpsHost;
        var eventsBefore = context.Events.Count;

        context.ViewModel.ReportEndpointCopy(succeeded: true);

        Assert.Equal(dirtyBefore, context.ViewModel.IsDirty);
        Assert.Equal(canApplyBefore, context.ViewModel.CanApply);
        Assert.Equal(endpointBefore, context.ViewModel.HttpsEndpoint);
        Assert.Equal(hostBefore, context.ViewModel.HttpsHost);
        Assert.Empty(context.Store.Writes);
        Assert.Equal(eventsBefore, context.Events.Count);
        Assert.Equal(0, context.Resources.ApplyCalls);
    }

    /// <summary>
    /// The copy confirmation is its own surface. Sharing the Apply banner would let "Copied!"
    /// overwrite the reason an Apply was refused.
    /// </summary>
    [Fact]
    public async Task TheCopyConfirmationDoesNotDisturbTheApplyBanner()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        EnableValidHttps(context.ViewModel);
        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        var applyMessage = context.ViewModel.ApplyMessage;
        var applyKind = context.ViewModel.ApplyResultKind;

        context.ViewModel.ReportEndpointCopy(succeeded: true);

        Assert.Equal(applyMessage, context.ViewModel.ApplyMessage);
        Assert.Equal(applyKind, context.ViewModel.ApplyResultKind);
        Assert.NotEqual(context.ViewModel.ApplyMessage, context.ViewModel.ToastMessage);
    }

    /// <summary>
    /// Three quick copies leave one toast, and the newest one owns the clock: the timer the first
    /// copy started must not hide the toast the third copy put up.
    /// </summary>
    [Fact]
    public async Task RepeatedCopiesKeepOneToastAndTheNewestClockWins()
    {
        var clock = new ManualClock();
        var context = CreateContext(clock: clock);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ReportEndpointCopy(succeeded: true);
        context.ViewModel.ReportEndpointCopy(succeeded: true);
        context.ViewModel.ReportEndpointCopy(succeeded: true);

        Assert.True(context.ViewModel.IsToastVisible);
        Assert.Equal("Copied!", context.ViewModel.ToastMessage);

        // The first copy's timer comes due. It was superseded, so it must hide nothing.
        clock.Fire(0);
        await Task.Delay(60);

        Assert.True(context.ViewModel.IsToastVisible);
    }

    /// <summary>The live timer does hide it, so the toast goes away on its own.</summary>
    [Fact]
    public async Task TheToastHidesItselfWhenItsOwnTimerComesDue()
    {
        var clock = new ManualClock();
        var context = CreateContext(clock: clock);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ReportEndpointCopy(succeeded: true);
        Assert.True(context.ViewModel.IsToastVisible);

        clock.Fire(clock.TimerCount - 1);
        await Task.Delay(60);

        Assert.False(context.ViewModel.IsToastVisible);
    }

    /// <summary>Closing the window while a toast is counting down must not leave a timer behind.</summary>
    [Fact]
    public async Task ClosingWhileAToastIsPendingCancelsItCleanly()
    {
        var clock = new ManualClock();
        var context = CreateContext(clock: clock);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.ReportEndpointCopy(succeeded: true);
        context.ViewModel.CancelTransientFeedback();

        clock.Fire(0);
        await Task.Delay(60);

        // Cancelling twice is what a close after a cancel would do, and it must not throw.
        context.ViewModel.CancelTransientFeedback();
    }

    // ================================================================ theme

    /// <summary>
    /// The glyph offers the destination, not the current state: dark shows a sun because pressing it
    /// turns the light on. Getting this backwards is the single most likely way to ship this control
    /// wrong, so it is asserted in both directions and in words as well as in the flag.
    /// </summary>
    [Fact]
    public void TheThemeButtonOffersTheOppositeOfWhatIsOnScreen()
    {
        var context = CreateContext();

        context.ViewModel.UpdateEffectiveTheme(isDark: true);
        Assert.True(context.ViewModel.ShowLightThemeAction);
        Assert.False(context.ViewModel.ShowDarkThemeAction);
        Assert.Equal("Enable light mode", context.ViewModel.ThemeActionText);

        context.ViewModel.UpdateEffectiveTheme(isDark: false);
        Assert.True(context.ViewModel.ShowDarkThemeAction);
        Assert.False(context.ViewModel.ShowLightThemeAction);
        Assert.Equal("Enable dark mode", context.ViewModel.ThemeActionText);
    }

    /// <summary>
    /// Toggling flips what is on screen, including from System - where the first press has to resolve
    /// against the effective theme or it appears to do nothing on a machine that already matches.
    /// </summary>
    [Theory]
    [InlineData(ThemePreference.System, true, ThemePreference.Light)]
    [InlineData(ThemePreference.System, false, ThemePreference.Dark)]
    [InlineData(ThemePreference.Dark, true, ThemePreference.Light)]
    [InlineData(ThemePreference.Light, false, ThemePreference.Dark)]
    public void TogglingTheThemeMovesToTheOppositeOfWhatIsShowing(
        ThemePreference start, bool effectiveDark, ThemePreference expected)
    {
        var preferences = new FakePreferences(savedTheme: start == ThemePreference.System ? null : start);
        var context = CreateContext(preferences: preferences);
        context.ViewModel.UpdateEffectiveTheme(effectiveDark);

        context.ViewModel.ToggleThemeCommand.Execute(null);

        Assert.Equal(expected, context.ViewModel.SelectedTheme);
        Assert.Equal(expected, preferences.SavedTheme);
    }

    /// <summary>
    /// Nothing is written until somebody chooses. Opening the window on a machine with no saved theme
    /// must not create one, or "follow Windows" would silently become a stored preference.
    /// </summary>
    [Fact]
    public async Task NoThemeIsSavedUntilOneIsChosen()
    {
        var preferences = new FakePreferences();
        var context = CreateContext(preferences: preferences);
        await context.ViewModel.RefreshAsync();

        Assert.Equal(ThemePreference.System, context.ViewModel.SelectedTheme);
        Assert.Null(preferences.SavedTheme);
        Assert.Equal(0, preferences.ThemeWrites);

        context.ViewModel.ToggleThemeCommand.Execute(null);

        Assert.Equal(1, preferences.ThemeWrites);
    }

    [Fact]
    public void ASavedThemeIsWhatTheWindowOpensIn()
    {
        var light = CreateContext(preferences: new FakePreferences(savedTheme: ThemePreference.Light));
        Assert.Equal(ThemePreference.Light, light.ViewModel.SelectedTheme);

        var dark = CreateContext(preferences: new FakePreferences(savedTheme: ThemePreference.Dark));
        Assert.Equal(ThemePreference.Dark, dark.ViewModel.SelectedTheme);
    }

    /// <summary>
    /// A theme change is a view preference and nothing else.
    ///
    /// It must not make the configuration dirty, must not enable Apply, must not write agent.json and
    /// must not reach the service, the certificate store or any system resource. That is the whole
    /// safety claim of the feature, so it is asserted against every seam at once.
    /// </summary>
    [Fact]
    public async Task ChangingTheThemeTouchesNothingBelongingToTheAgent()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();

        var dirtyBefore = context.ViewModel.IsDirty;
        var canApplyBefore = context.ViewModel.CanApply;
        var hostBefore = context.ViewModel.HttpsHost;
        var httpsBefore = context.ViewModel.HttpsEnabled;
        var pipeBefore = context.ViewModel.NamedPipeEnabled;
        var certificateBefore = context.ViewModel.CertificateThumbprint;
        var eventsBefore = context.Events.Count;

        context.ViewModel.ToggleThemeCommand.Execute(null);
        context.ViewModel.ToggleThemeCommand.Execute(null);

        Assert.Equal(dirtyBefore, context.ViewModel.IsDirty);
        Assert.Equal(canApplyBefore, context.ViewModel.CanApply);
        Assert.Equal(hostBefore, context.ViewModel.HttpsHost);
        Assert.Equal(httpsBefore, context.ViewModel.HttpsEnabled);
        Assert.Equal(pipeBefore, context.ViewModel.NamedPipeEnabled);
        Assert.Equal(certificateBefore, context.ViewModel.CertificateThumbprint);

        Assert.Empty(context.Store.Writes);
        Assert.Equal(eventsBefore, context.Events.Count);
        Assert.Empty(context.Resources.RemoveRequests);
        Assert.Equal(0, context.Resources.ApplyCalls);
    }

    /// <summary>The action text follows the language, like everything else on the window.</summary>
    [Fact]
    public void TheThemeActionTextIsLocalised()
    {
        var context = CreateContext();
        context.ViewModel.UpdateEffectiveTheme(isDark: true);

        Assert.Equal("Enable light mode", context.ViewModel.ThemeActionText);

        context.ViewModel.SelectedLanguage = UiLanguagePreference.PtBr;

        Assert.Equal("Ativar modo claro", context.ViewModel.ThemeActionText);
    }

    // ================================================================ status presentation

    /// <summary>
    /// The card carries a short localised phrase and the platform sentence moves to the tooltip.
    ///
    /// The adapter detail is written in English by infrastructure and names an AppId or a rule; it
    /// ran to several lines inside a quarter-width column and put English inside the Portuguese
    /// window. Moving it is presentation - the classification is untouched and nothing is lost.
    /// </summary>
    [Fact]
    public async Task StatusColumnsShowAShortStateAndKeepTheTechnicalDetailOnTheTooltip()
    {
        var context = CreateContext(document: HttpsDocument());
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(
                AgentResourceOwnership.ForeignOwner,
                "Port 5199 is bound by another application (AppId {ad073820-0000-0000-0000-000000000000})"),
            new AgentResourceState(AgentResourceOwnership.Absent),
            new AgentResourceState(
                AgentResourceOwnership.ForeignOwner,
                "A rule named NutManager Agent HTTPS exists but is not grouped under NutManager."));
        await context.ViewModel.RefreshAsync();

        var binding = context.ViewModel.ResourceStatus[0];
        var reservation = context.ViewModel.ResourceStatus[1];
        var firewall = context.ViewModel.ResourceStatus[2];

        Assert.Equal("Owned by another application", binding.Detail);
        Assert.Equal("Not configured", reservation.Detail);

        // A rule that is not ours is most often one somebody left behind, not a rival product.
        Assert.Equal("Existing unmanaged rule", firewall.Detail);

        // Nothing inline is the adapter sentence, and no AppId reaches the card.
        Assert.All(
            context.ViewModel.ResourceStatus,
            item => Assert.DoesNotContain("AppId", item.Detail ?? string.Empty, StringComparison.Ordinal));

        // The detail is not lost: it is on the tooltip, whole.
        Assert.Contains("AppId", binding.TechnicalDetail);
        Assert.Contains("AppId", binding.TooltipText);
        Assert.Contains("not grouped under NutManager", firewall.TechnicalDetail);

        // And the classification behind it is exactly what it was.
        Assert.Equal(AgentDiagnosticState.Attention, binding.State);
        Assert.Equal(AgentDiagnosticState.Error, reservation.State);
        Assert.Equal(AgentDiagnosticState.Attention, firewall.State);
    }

    /// <summary>
    /// Portuguese agrees with the noun. "URL Reservation configurado" is wrong in the language this
    /// product is mostly read in, and one shared string cannot express both forms.
    /// </summary>
    [Fact]
    public async Task ThePortugueseStatusTextAgreesWithTheResourceItDescribes()
    {
        var context = CreateContext(document: HttpsDocument(), language: UiLanguagePreference.PtBr);
        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager));
        await context.ViewModel.RefreshAsync();

        Assert.Equal("Configurado", context.ViewModel.ResourceStatus[0].Detail);
        Assert.Equal("Configurada", context.ViewModel.ResourceStatus[1].Detail);
        Assert.Equal("Configurado", context.ViewModel.ResourceStatus[2].Detail);
    }

    /// <summary>
    /// With HTTPS deliberately off, every resource reads as switched off rather than as broken, and
    /// says so in the window language rather than borrowing a diagnostics string.
    /// </summary>
    [Fact]
    public async Task DisabledHttpsReportsEveryResourceAsSwitchedOff()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        Assert.Equal(4, context.ViewModel.ResourceStatus.Count);
        Assert.All(context.ViewModel.ResourceStatus, item =>
        {
            Assert.Equal(AgentDiagnosticState.NotConfigured, item.State);
            Assert.Equal("HTTPS disabled", item.Detail);
        });
    }

    /// <summary>
    /// The restart marker is a state, not a sentence appended to the apply message, and it never
    /// causes a restart of its own.
    /// </summary>
    [Fact]
    public async Task SavingWhileTheServiceRunsRaisesTheRestartMarkerWithoutRestarting()
    {
        var context = CreateContext(serviceState: AgentServiceState.Running);
        await context.ViewModel.RefreshAsync();

        Assert.False(context.ViewModel.RestartRequired);

        EnableValidHttps(context.ViewModel);
        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.True(context.ViewModel.RestartRequired);
        Assert.Equal(AgentConfigConfirmation.RestartService, context.ViewModel.PendingConfirmation);

        // Offered, not performed.
        Assert.Equal(0, context.Service.RestartCalls);

        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, context.Service.RestartCalls);
        Assert.False(context.ViewModel.RestartRequired);
    }

    // ================================================================ certificate import

    /// <summary>
    /// A successful import selects what was imported and re-derives everything the details panel
    /// shows, without the window being reopened.
    /// </summary>
    [Fact]
    public async Task ImportingACertificateSelectsItAndRefreshesTheDetails()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.ViewModel.HttpsHost = "nut-server.example.local";

        var imported = new AgentCertificateSummary(
            "0123456789ABCDEF0123456789ABCDEF01234567",
            "CN=nut-server.example.local",
            "CN=Test CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(2),
            HasPrivateKey: true,
            SupportsServerAuthentication: true,
            SubjectAlternativeNames: ["nut-server.example.local"]);
        context.Importer.Result = AgentCertificateImportResult.Imported(imported);

        var result = await context.ViewModel.ImportCertificateAsync("C:/certs/backup.pfx", password: null);

        Assert.Equal(AgentCertificateImportOutcome.Imported, result.Outcome);
        Assert.Equal(imported.Thumbprint, context.ViewModel.SelectedCertificate?.Thumbprint);
        Assert.Equal(imported.Thumbprint, context.ViewModel.CertificateThumbprint);
        Assert.Equal("CN=nut-server.example.local", context.ViewModel.CertificateSubject);
        Assert.Contains("nut-server.example.local", context.ViewModel.CertificateSubjectAlternativeNames);
        Assert.True(context.ViewModel.HttpsIsValid);
    }

    /// <summary>
    /// Rights and file validity are different problems with different fixes. An import Windows refused
    /// must not be reported as a bad file, which would send an administrator to their certificate
    /// authority for a file that was always fine.
    /// </summary>
    [Fact]
    public async Task AnImportRefusedForRightsIsNotReportedAsABadFile()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        context.Importer.Result = AgentCertificateImportResult.From(
            AgentCertificateImportOutcome.AccessDenied, "CryptographicException (0x80090010)");

        await context.ViewModel.ImportCertificateAsync("C:/certs/server.pfx", password: null);

        var message = context.ViewModel.CertificateFeedbackMessage;
        Assert.NotNull(message);
        Assert.Contains("denied", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invalid certificate file", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("critical", context.ViewModel.CertificateFeedbackStateClass);

        // The technical detail is one hover away rather than inline: it is a type and a code for a
        // support conversation, and on the card it would push a two-line message to three.
        Assert.DoesNotContain("0x80090010", message);
        Assert.Equal("CryptographicException (0x80090010)", context.ViewModel.CertificateFeedbackDetail);

        // A later success must not leave the previous failure's detail hanging on the tooltip.
        context.Importer.Result = AgentCertificateImportResult.Imported(
            Assert.Single(context.Certificates.List()));
        await context.ViewModel.ImportCertificateAsync("C:/certs/server.pfx", password: null);
        Assert.Null(context.ViewModel.CertificateFeedbackDetail);
    }

    /// <summary>
    /// A failure the adapter never anticipated is reported rather than escaping into an async void
    /// click handler and taking the window down with it.
    /// </summary>
    [Fact]
    public async Task AnUnanticipatedImportFailureIsReportedRatherThanThrown()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        context.ViewModel.HttpsEnabled = true;
        context.Importer.Failure = new InvalidOperationException("something nobody planned for");

        var result = await context.ViewModel.ImportCertificateAsync("C:/certs/server.pfx", password: null);

        Assert.Equal(AgentCertificateImportOutcome.Failed, result.Outcome);
        Assert.True(context.ViewModel.ShowCertificateFeedback);
        Assert.Contains("InvalidOperationException", context.ViewModel.CertificateFeedbackDetail);
        Assert.Equal("critical", context.ViewModel.CertificateFeedbackStateClass);

        // The busy flag is cleared on the way out, so the window is usable again rather than frozen.
        Assert.False(context.ViewModel.IsBusy);
        Assert.True(context.ViewModel.CanImportCertificate);
    }

    /// <summary>
    /// The password is an argument and never becomes state. It reaches the adapter, and nothing that
    /// is shown, saved or reported afterwards contains it.
    /// </summary>
    [Fact]
    public async Task AnImportPasswordNeverReachesViewStateOrConfiguration()
    {
        const string Password = "correct horse battery staple";

        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        context.Importer.Result = AgentCertificateImportResult.From(
            AgentCertificateImportOutcome.PasswordIncorrect);

        await context.ViewModel.ImportCertificateAsync("C:/certs/server.pfx", Password);

        Assert.Equal(Password, context.Importer.LastPassword);
        Assert.DoesNotContain(Password, context.ViewModel.CertificateFeedbackMessage ?? string.Empty);
        Assert.DoesNotContain(Password, context.ViewModel.ApplyMessage ?? string.Empty);
        Assert.DoesNotContain(Password, context.ViewModel.HttpsEndpoint);
        Assert.All(context.Store.Writes, document => Assert.DoesNotContain(
            Password, document.CertificateThumbprint ?? string.Empty));
    }

    // ================================================================ language

    /// <summary>
    /// Changing the language re-renders the window and touches nothing administrative: no write to
    /// agent.json and no further call into any of the machine adapters.
    /// </summary>
    [Fact]
    public async Task ChangingLanguageRelocalisesTheWindowWithoutTouchingTheMachine()
    {
        var context = CreateContext(document: HttpsDocument());
        await context.ViewModel.RefreshAsync();

        var eventsBefore = context.Events.Count;

        context.ViewModel.SelectedLanguage = UiLanguagePreference.PtBr;

        Assert.Equal(UiLanguagePreference.PtBr, context.ViewModel.Strings.Language);
        Assert.Equal("Transporte", context.ViewModel.Strings["Transport.Title"]);

        Assert.Empty(context.Store.Writes);
        Assert.Equal(eventsBefore, context.Events.Count);

        // The status strip carries text composed when it was built, so it must be rebuilt rather than
        // left in the previous language.
        Assert.NotEmpty(context.ViewModel.ResourceStatus);
        Assert.All(
            context.ViewModel.ResourceStatus,
            item => Assert.DoesNotContain("Not configured", item.StatusText, StringComparison.Ordinal));
    }

    /// <summary>
    /// The two selector states track the language and, critically, ignore the <c>false</c> that a
    /// radio group writes to the option it is unchecking.
    ///
    /// That write is what the previous ComboBox turned into a silent language change at start-up: it
    /// overwrote the operator's saved preference before they had touched anything.
    /// </summary>
    [Fact]
    public void TheLanguageSelectorNeverChangesTheLanguageByBeingUnchecked()
    {
        var preferences = new FakePreferences(UiLanguagePreference.EnUs);
        var context = CreateContext(preferences: preferences, language: null);

        Assert.True(context.ViewModel.IsEnglishSelected);
        Assert.False(context.ViewModel.IsPortugueseSelected);

        // What a radio group writes to the losing option. It must change nothing.
        context.ViewModel.IsEnglishSelected = false;
        context.ViewModel.IsPortugueseSelected = false;

        Assert.Equal(UiLanguagePreference.EnUs, context.ViewModel.SelectedLanguage);
        Assert.Equal(UiLanguagePreference.EnUs, preferences.Saved);
        Assert.Equal(0, preferences.Writes);

        // Choosing the other one does change it, exactly once.
        context.ViewModel.IsPortugueseSelected = true;

        Assert.Equal(UiLanguagePreference.PtBr, context.ViewModel.SelectedLanguage);
        Assert.True(context.ViewModel.IsPortugueseSelected);
        Assert.False(context.ViewModel.IsEnglishSelected);
        Assert.Equal(1, preferences.Writes);
    }

    /// <summary>
    /// Every computed string follows the language, not just the ones somebody listed. Three of them -
    /// the two transport pills and the last-transport notice - stayed in the previous language when
    /// this was a hand-written notification list.
    /// </summary>
    [Fact]
    public async Task EveryComputedStringFollowsTheLanguage()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        Assert.Equal("Active", context.ViewModel.NamedPipeStatusText);

        context.ViewModel.SelectedLanguage = UiLanguagePreference.PtBr;

        Assert.Equal("Ativo", context.ViewModel.NamedPipeStatusText);
        Assert.Equal("Inativo", context.ViewModel.HttpsStatusText);
        Assert.Contains("transporte", context.ViewModel.LastTransportNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Serviço", context.ViewModel.ServiceFooterText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preference is remembered outside the service's configuration. Which language somebody reads
    /// is not a property of how the agent listens, and it must never ride an administrative write.
    /// </summary>
    [Fact]
    public async Task TheLanguagePreferenceIsSavedOutsideTheServiceConfiguration()
    {
        var preferences = new FakePreferences();
        var context = CreateContext(preferences: preferences);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.SelectedLanguage = UiLanguagePreference.PtBr;

        Assert.Equal(UiLanguagePreference.PtBr, preferences.Saved);
        Assert.Equal(1, preferences.Writes);
        Assert.Empty(context.Store.Writes);
    }

    /// <summary>A saved preference is what the window opens in, ahead of the Windows culture.</summary>
    [Fact]
    public void ASavedLanguagePreferenceWinsOverTheWindowsCulture()
    {
        var context = CreateContext(preferences: new FakePreferences(UiLanguagePreference.PtBr), language: null);

        Assert.Equal(UiLanguagePreference.PtBr, context.ViewModel.SelectedLanguage);
        Assert.Equal("Transporte", context.ViewModel.Strings["Transport.Title"]);
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
        // A valid endpoint, because the strip only reports on the machine once there is something to
        // ask about. Enabling HTTPS alone leaves the draft incomplete, and the strip then says so
        // rather than claiming the resources are absent.
        var absent = CreateContext(serviceState: AgentServiceState.NotInstalled);
        await absent.ViewModel.RefreshAsync();
        EnableValidHttps(absent.ViewModel);

        var absentListener = absent.ViewModel.ResourceStatus.Last();
        Assert.Equal(AgentDiagnosticState.Error, absentListener.State);
        Assert.Contains("not installed", absentListener.TechnicalDetail, StringComparison.OrdinalIgnoreCase);

        var stopped = CreateContext(serviceState: AgentServiceState.Stopped);
        await stopped.ViewModel.RefreshAsync();
        EnableValidHttps(stopped.ViewModel);

        var stoppedListener = stopped.ViewModel.ResourceStatus.Last();
        Assert.Contains("stopped", stoppedListener.TechnicalDetail, StringComparison.OrdinalIgnoreCase);

        // Stopped and absent are different machine states and must not collapse into one.
        //
        // The card now says "Listener unavailable" for both, because that is what an operator sees
        // either way and a quarter-width column has no room for the difference. The difference is
        // still carried, and carried twice: a red error against an amber warning, which is the part
        // read at a glance, and the precise sentence on the tooltip, which is the part read when
        // deciding what to do. What must never happen is the two becoming one state.
        Assert.NotEqual(absentListener.State, stoppedListener.State);
        Assert.NotEqual(absentListener.TechnicalDetail, stoppedListener.TechnicalDetail);
        Assert.NotEqual(absentListener.TooltipText, stoppedListener.TooltipText);
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

        // A complete draft, so the window actually queried Windows and found the binding absent.
        // Without one there is nothing to query for, and the strip says "not checked" instead.
        EnableValidHttps(context.ViewModel);

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
        context.ViewModel.HttpsHost = "other.example.local";
        context.ViewModel.HttpsPort = 5199;
        context.ViewModel.SelectedCertificate = Assert.Single(context.ViewModel.Certificates);

        Assert.False(context.ViewModel.HttpsIsValid);

        // One sentence. Three stacked warnings cost three lines of a 600px window and still leave the
        // operator deciding which to act on first.
        var message = Assert.IsType<string>(context.ViewModel.HttpsValidationMessage);
        Assert.DoesNotContain(". ", message.TrimEnd('.'), StringComparison.Ordinal);
        Assert.Equal("The certificate does not match the specified host.", message);

        // The card names neither the host nor what the certificate covers - listing a subject and
        // every alternative name made the most common problem the longest line on the screen. Both
        // are on the tooltip, which is also where the details panel and Diagnostics point.
        Assert.DoesNotContain(context.ViewModel.HttpsHost, message, StringComparison.Ordinal);
        Assert.Contains(
            context.ViewModel.HttpsHost, context.ViewModel.CertificateFeedbackDetail, StringComparison.Ordinal);
        Assert.Contains(
            context.ViewModel.SelectedCertificate!.DisplayName,
            context.ViewModel.CertificateFeedbackDetail,
            StringComparison.Ordinal);
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

        context.ViewModel.HttpsHost = "nut-server.example.local";
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
        context.ViewModel.HttpsHost = "nut-server.example.local";
        var imported = new AgentCertificateSummary(
            "1123456789ABCDEF0123456789ABCDEF01234567",
            "CN=nut-server.example.local",
            "CN=Imported Test CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            HasPrivateKey: true,
            SupportsServerAuthentication: true,
            SubjectAlternativeNames: ["nut-server.example.local"]);
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
        context.ViewModel.HttpsHost = "nut-server.example.local";
        var imported = new AgentCertificateSummary(
            "2123456789ABCDEF0123456789ABCDEF01234567",
            "CN=other.example.local",
            "CN=Imported Test CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            HasPrivateKey: true,
            SupportsServerAuthentication: true,
            SubjectAlternativeNames: ["other.example.local"]);
        context.Importer.Result = AgentCertificateImportResult.Imported(imported);

        await context.ViewModel.ImportCertificateAsync("certificate.cer", password: null);

        Assert.Equal(imported.Thumbprint, context.ViewModel.CertificateThumbprint);
        Assert.False(context.ViewModel.HttpsIsValid);
        Assert.False(context.ViewModel.CanApply);
        Assert.Equal("warning", context.ViewModel.CertificateImportStateClass);
        Assert.Contains("does not match the specified host", context.ViewModel.CertificateImportMessage, StringComparison.OrdinalIgnoreCase);
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

    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static TestContext CreateContext(
        AgentTransportConfigurationDocument? document = null,
        AgentServiceState serviceState = AgentServiceState.Stopped,
        AgentMachineRole groupRole = AgentMachineRole.StandaloneWorkstation,
        bool groupExists = false,
        FakePreferences? preferences = null,
        UiLanguagePreference? language = UiLanguagePreference.EnUs,
        bool withCertificate = true,
        TimeProvider? clock = null,
        FakeInventory? inventory = null,
        FakeListener? listener = null)
    {
        var events = new List<string>();
        var store = new FakeStore(document ?? new AgentTransportConfigurationDocument(), events);
        var groups = new FakeGroups(groupRole, groupExists);
        var service = new FakeService(serviceState);
        var resources = new FakeResources(events);
        var certificate = new AgentCertificateSummary(
            Thumbprint,
            "CN=nut-server.example.local",
            "CN=Test CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            HasPrivateKey: true,
            SupportsServerAuthentication: true,
            SubjectAlternativeNames: ["nut-server.example.local"]);
        var certificates = withCertificate ? new FakeCertificates(certificate) : new FakeCertificates();
        var importer = new FakeCertificateImporter(certificates);
        inventory ??= new FakeInventory();
        var uiPreferences = preferences ?? new FakePreferences();
        listener ??= new FakeListener();
        var viewModel = new AgentConfigViewModel(
            store, groups, service, resources, certificates, inventory, language,
            timeProvider: clock,
            certificateImporter: importer,
            preferences: uiPreferences,
            listenerProbe: listener);

        return new TestContext(
            viewModel, store, groups, service, resources, certificates, importer, listener, events);
    }

    // ---------------------------------------------------------------- T42: settings presentation

    /// <summary>
    /// The start type and the account shown on the Agent tab are the ones the service control manager
    /// reported, not defaults chosen by the screen.
    ///
    /// Both read "Desconhecido" on a real server while the service was installed and running, because
    /// the values came from a WMI query that failed quietly. Nothing may put a plausible-looking value
    /// in their place: an account this window invents is worse than one it admits it could not read.
    /// </summary>
    [Fact]
    public async Task TheAgentTabReportsTheStartTypeAndAccountTheSnapshotCarries()
    {
        var context = CreateContext();
        context.Service.StartType = AgentServiceStartType.Manual;
        context.Service.Account = @"EXAMPLE\\svc_nutmanager";

        await context.ViewModel.RefreshAsync();

        Assert.Equal("Manual", context.ViewModel.ServiceStartTypeText);
        Assert.Equal(@"EXAMPLE\\svc_nutmanager", context.ViewModel.ServiceAccountText);
    }

    [Fact]
    public async Task AnAutomaticServiceRunningAsLocalSystemReadsBackAsItself()
    {
        var context = CreateContext();
        context.Service.StartType = AgentServiceStartType.Automatic;
        context.Service.Account = "LocalSystem";

        await context.ViewModel.RefreshAsync();

        Assert.Equal("Automatic", context.ViewModel.ServiceStartTypeText);
        Assert.Equal("LocalSystem", context.ViewModel.ServiceAccountText);

        // The same fact drives the startup switch, so the two tabs cannot disagree.
        Assert.True(context.ViewModel.StartsWithWindows);
    }

    /// <summary>
    /// A configuration that genuinely could not be read still says so. The fix was to stop the read
    /// from failing, not to stop it from being able to fail.
    /// </summary>
    [Fact]
    public async Task AnUnreadableServiceConfigurationSaysUnknownRatherThanGuessing()
    {
        var context = CreateContext();
        context.Service.StartType = AgentServiceStartType.Unknown;
        context.Service.Account = string.Empty;
        context.Service.Failure = "Win32 error 5: Access is denied.";
        context.Service.QueryErrorCode = 5;

        await context.ViewModel.RefreshAsync();

        Assert.Equal("Unknown", context.ViewModel.ServiceStartTypeText);
        Assert.Equal("Unknown", context.ViewModel.ServiceAccountText);
        Assert.False(context.ViewModel.StartsWithWindows);

        var diagnostic = Assert.Single(
            context.ViewModel.Diagnostics,
            item => item.Label == context.ViewModel.Strings["Diagnostics.AgentRegistered"]);
        Assert.Equal(AgentDiagnosticState.Attention, diagnostic.State);
        Assert.Contains("Win32 error 5", diagnostic.TechnicalDetail, StringComparison.Ordinal);
    }

    /// <summary>The runtimes the inventory found are the runtimes About reports.</summary>
    [Fact]
    public async Task AboutReportsTheRuntimesTheInventoryFound()
    {
        var context = CreateContext();

        await context.ViewModel.RefreshAsync();

        Assert.Equal("10.0.0", context.ViewModel.AboutDotNetRuntime);
        Assert.Equal("10.0.0", context.ViewModel.AboutAspNetCoreRuntime);
    }

    /// <summary>
    /// A runtime that truly cannot be determined is reported as unknown. This is the fail-safe, and it
    /// is meant to be rare: on a machine with the runtimes installed, both resolve.
    /// </summary>
    [Fact]
    public async Task AbsentRuntimesFallBackToUnknown()
    {
        var context = CreateContext(inventory: new FakeInventory(dotNet: null, aspNetCore: null));

        await context.ViewModel.RefreshAsync();

        Assert.Equal("Unknown", context.ViewModel.AboutDotNetRuntime);
        Assert.Equal("Unknown", context.ViewModel.AboutAspNetCoreRuntime);
    }

    /// <summary>
    /// Each half of the segmented control selects its own theme outright, and nothing else: it writes
    /// a user preference and does not put the configuration draft into a state that needs applying.
    /// </summary>
    [Fact]
    public async Task TheThemeControlWritesThePreferenceAndLeavesTheDraftAlone()
    {
        var preferences = new FakePreferences();
        var context = CreateContext(preferences: preferences);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.SelectDarkThemeCommand.Execute(null);
        Assert.Equal(ThemePreference.Dark, context.ViewModel.SelectedTheme);
        Assert.True(context.ViewModel.IsDarkThemeSelected);

        context.ViewModel.SelectLightThemeCommand.Execute(null);
        Assert.Equal(ThemePreference.Light, context.ViewModel.SelectedTheme);
        Assert.False(context.ViewModel.IsDarkThemeSelected);

        Assert.False(context.ViewModel.IsDirty);
    }

    /// <summary>
    /// The switch reports the theme as well as setting it, so reading it back cannot drift from what
    /// was chosen.
    /// </summary>
    [Fact]
    public void TheThemeControlReadsBackTheChosenTheme()
    {
        var context = CreateContext();

        context.ViewModel.SelectedTheme = ThemePreference.Dark;
        Assert.True(context.ViewModel.IsDarkThemeSelected);

        context.ViewModel.SelectedTheme = ThemePreference.Light;
        Assert.False(context.ViewModel.IsDarkThemeSelected);
    }

    /// <summary>Settings, the terms, and back - and the draft controls stay out of both.</summary>
    [Fact]
    public void TheTermsPageIsReachedFromSettingsAndReturnsToIt()
    {
        var context = CreateContext();
        var viewModel = context.ViewModel;

        Assert.True(viewModel.ShowConfiguration);
        Assert.True(viewModel.ShowActionBar);

        viewModel.HeaderActionCommand.Execute(null);
        Assert.True(viewModel.ShowSettings);
        Assert.False(viewModel.ShowActionBar);

        viewModel.OpenTermsCommand.Execute(null);
        Assert.True(viewModel.ShowTerms);
        Assert.False(viewModel.ShowSettings);
        Assert.False(viewModel.ShowActionBar);

        viewModel.CloseTermsCommand.Execute(null);
        Assert.True(viewModel.ShowSettings);
        Assert.False(viewModel.ShowTerms);
        Assert.False(viewModel.ShowActionBar);

        viewModel.HeaderActionCommand.Execute(null);
        Assert.True(viewModel.ShowConfiguration);
        Assert.True(viewModel.ShowActionBar);
    }

    /// <summary>Diagnostics is not settings: it keeps the draft controls it always had.</summary>
    [Fact]
    public void DiagnosticsKeepsTheDraftControls()
    {
        var context = CreateContext();

        context.ViewModel.ToggleDiagnosticsCommand.Execute(null);

        Assert.True(context.ViewModel.ShowDiagnostics);
        Assert.True(context.ViewModel.ShowActionBar);
    }

    // ---------------------------------------------------------------- T42: navigation and language

    /// <summary>
    /// The selector opens on the saved language and does not write anything on the way.
    ///
    /// This is the regression that made the control a flyout in the first place: a list control
    /// assigns its selection while it materialises, and that assignment reached the preference store.
    /// The saved value must survive a window that is merely opened and closed.
    /// </summary>
    [Fact]
    public void OpeningTheWindowDoesNotWriteTheLanguagePreference()
    {
        var preferences = new FakePreferences(saved: UiLanguagePreference.EnUs);
        var context = CreateContext(preferences: preferences, language: null);

        Assert.Equal(UiLanguagePreference.EnUs, context.ViewModel.SelectedLanguage);
        Assert.Equal(UiLanguagePreference.EnUs, context.ViewModel.SelectedLanguageOption?.Value);
        Assert.Equal(0, preferences.Writes);
        Assert.Equal(UiLanguagePreference.EnUs, preferences.Saved);
    }

    /// <summary>The list names each language in its own language, exactly as the desktop lists them.</summary>
    [Fact]
    public void TheLanguageListNamesEachLanguageInItself()
    {
        var context = CreateContext();

        Assert.Collection(
            context.ViewModel.LanguageOptions,
            option =>
            {
                Assert.Equal(UiLanguagePreference.PtBr, option.Value);
                Assert.Equal("Português (Brasil)", option.Title);
            },
            option =>
            {
                Assert.Equal(UiLanguagePreference.EnUs, option.Value);
                Assert.Equal("English (United States)", option.Title);
            });
    }

    /// <summary>
    /// Choosing a language applies it at once and saves it, and touches nothing that belongs to the
    /// configuration draft.
    /// </summary>
    [Fact]
    public async Task ChoosingALanguageAppliesAndPersistsItWithoutTouchingTheDraft()
    {
        var preferences = new FakePreferences();
        var context = CreateContext(preferences: preferences, language: UiLanguagePreference.PtBr);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.SelectedLanguageOption =
            context.ViewModel.LanguageOptions.Single(option => option.Value == UiLanguagePreference.EnUs);

        Assert.Equal(UiLanguagePreference.EnUs, context.ViewModel.SelectedLanguage);
        Assert.Equal("Settings", context.ViewModel.Strings["Settings.Title"]);
        Assert.Equal(UiLanguagePreference.EnUs, preferences.Saved);
        Assert.Equal(1, preferences.Writes);

        // A window preference is not a configuration change.
        Assert.False(context.ViewModel.IsDirty);
        Assert.Empty(context.Store.Writes);
    }

    /// <summary>A language set from elsewhere brings the selector with it, and is not echoed back.</summary>
    [Fact]
    public void TheSelectorFollowsALanguageChangedElsewhere()
    {
        var preferences = new FakePreferences();
        var context = CreateContext(preferences: preferences, language: UiLanguagePreference.PtBr);

        context.ViewModel.SelectedLanguage = UiLanguagePreference.EnUs;

        Assert.Equal(UiLanguagePreference.EnUs, context.ViewModel.SelectedLanguageOption?.Value);
        Assert.Equal(1, preferences.Writes);
    }

    /// <summary>
    /// The header button offers the action that is actually available: settings from the configuration
    /// surface and from diagnostics, home from settings and from the terms.
    /// </summary>
    [Fact]
    public void TheHeaderButtonOffersSettingsUntilYouAreInThem()
    {
        var context = CreateContext();
        var viewModel = context.ViewModel;

        Assert.True(viewModel.ShowSettingsAction);
        Assert.Equal("Settings", viewModel.HeaderActionText);

        viewModel.HeaderActionCommand.Execute(null);
        Assert.True(viewModel.ShowSettings);
        Assert.True(viewModel.ShowHomeAction);
        Assert.Equal("Home", viewModel.HeaderActionText);

        // From the terms it still goes home, and home is the configuration surface - not settings.
        viewModel.OpenTermsCommand.Execute(null);
        Assert.True(viewModel.ShowHomeAction);
        viewModel.HeaderActionCommand.Execute(null);
        Assert.True(viewModel.ShowConfiguration);

        // Diagnostics keeps its own labelled control for getting back, so the gear still offers the
        // one thing it is for.
        viewModel.ToggleDiagnosticsCommand.Execute(null);
        Assert.True(viewModel.ShowDiagnostics);
        Assert.True(viewModel.ShowSettingsAction);
        viewModel.HeaderActionCommand.Execute(null);
        Assert.True(viewModel.ShowSettings);
    }

    /// <summary>
    /// Going home is navigation. An operator who edits a field, looks at a preference and comes back
    /// finds the edit where they left it, with Apply still offering to save it.
    /// </summary>
    [Fact]
    public async Task GoingHomeKeepsTheDraftExactlyAsItWas()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();

        EnableValidHttps(context.ViewModel);
        Assert.True(context.ViewModel.IsDirty);
        Assert.True(context.ViewModel.CanApply);

        var host = context.ViewModel.HttpsHost;
        var port = context.ViewModel.HttpsPort;
        var certificate = context.ViewModel.SelectedCertificate;

        context.ViewModel.HeaderActionCommand.Execute(null);
        Assert.True(context.ViewModel.ShowSettings);

        context.ViewModel.HeaderActionCommand.Execute(null);
        Assert.True(context.ViewModel.ShowConfiguration);

        Assert.True(context.ViewModel.IsDirty);
        Assert.True(context.ViewModel.CanApply);
        Assert.Equal(host, context.ViewModel.HttpsHost);
        Assert.Equal(port, context.ViewModel.HttpsPort);
        Assert.Same(certificate, context.ViewModel.SelectedCertificate);

        // Nothing was saved and nothing was rolled back: the trip wrote no configuration at all.
        Assert.Empty(context.Store.Writes);
    }

    /// <summary>
    /// The panel you were reading is the panel you come back to. Nothing resets it to the first one.
    /// </summary>
    [Fact]
    public void TheSettingsPanelSurvivesATripHome()
    {
        var context = CreateContext();
        var viewModel = context.ViewModel;

        viewModel.HeaderActionCommand.Execute(null);
        viewModel.SelectAboutTabCommand.Execute(null);
        Assert.True(viewModel.IsAboutTab);

        viewModel.HeaderActionCommand.Execute(null);
        Assert.True(viewModel.ShowConfiguration);

        viewModel.HeaderActionCommand.Execute(null);
        Assert.True(viewModel.ShowSettings);
        Assert.True(viewModel.IsAboutTab);
        Assert.False(viewModel.IsGeneralTab);
    }

    /// <summary>One panel at a time, and always exactly one.</summary>
    [Fact]
    public void ExactlyOneSettingsPanelIsShown()
    {
        var context = CreateContext();
        var viewModel = context.ViewModel;

        foreach (var select in new Action[]
                 {
                     () => viewModel.SelectGeneralTabCommand.Execute(null),
                     () => viewModel.SelectAppearanceTabCommand.Execute(null),
                     () => viewModel.SelectAgentTabCommand.Execute(null),
                     () => viewModel.SelectAboutTabCommand.Execute(null),
                 })
        {
            select();

            var shown = new[]
            {
                viewModel.IsGeneralTab,
                viewModel.IsAppearanceTab,
                viewModel.IsAgentTab,
                viewModel.IsAboutTab,
            };

            Assert.Single(shown, flag => flag);
        }
    }

    [Fact]
    public async Task AgentTransportSummaryUsesTheCurrentConfigurationForEveryCombination()
    {
        var namedPipeOnly = CreateContext();
        await namedPipeOnly.ViewModel.RefreshAsync();
        Assert.Equal("SMB (Named Pipe)", namedPipeOnly.ViewModel.ActiveTransportsText);
        Assert.Equal("None", namedPipeOnly.ViewModel.HttpsPortText);

        var httpsOnly = CreateContext(document: new AgentTransportConfigurationDocument
        {
            NamedPipeEnabled = false,
            HttpsEnabled = true,
            HttpsPrefix = "https://nut-server.example.local:5199/",
            CertificateThumbprint = Thumbprint,
        });
        await httpsOnly.ViewModel.RefreshAsync();
        Assert.Equal("HTTPS", httpsOnly.ViewModel.ActiveTransportsText);
        Assert.Equal("5199", httpsOnly.ViewModel.HttpsPortText);

        var both = CreateContext(document: HttpsDocument());
        await both.ViewModel.RefreshAsync();
        Assert.Equal("SMB (Named Pipe), HTTPS", both.ViewModel.ActiveTransportsText);
        Assert.Equal("5199", both.ViewModel.HttpsPortText);
    }

    [Fact]
    public async Task TransportSummaryNotifiesAfterLoadApplyAndReset()
    {
        var loaded = CreateContext(document: HttpsDocument());
        var loadChanges = new List<string?>();
        loaded.ViewModel.PropertyChanged += (_, args) => loadChanges.Add(args.PropertyName);

        await loaded.ViewModel.RefreshAsync();

        Assert.Contains(nameof(AgentConfigViewModel.ActiveTransportsText), loadChanges);
        Assert.Contains(nameof(AgentConfigViewModel.HttpsPortText), loadChanges);

        var applied = CreateContext();
        await applied.ViewModel.RefreshAsync();
        EnableValidHttps(applied.ViewModel);
        var applyChanges = new List<string?>();
        applied.ViewModel.PropertyChanged += (_, args) => applyChanges.Add(args.PropertyName);

        await applied.ViewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Contains(nameof(AgentConfigViewModel.ActiveTransportsText), applyChanges);
        Assert.Contains(nameof(AgentConfigViewModel.HttpsPortText), applyChanges);
        Assert.Equal("SMB (Named Pipe), HTTPS", applied.ViewModel.ActiveTransportsText);

        var reset = CreateContext(document: HttpsDocument());
        await reset.ViewModel.RefreshAsync();
        var resetChanges = new List<string?>();
        reset.ViewModel.PropertyChanged += (_, args) => resetChanges.Add(args.PropertyName);

        reset.ViewModel.ResetHttpsCommand.Execute(null);
        await reset.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(nameof(AgentConfigViewModel.ActiveTransportsText), resetChanges);
        Assert.Contains(nameof(AgentConfigViewModel.HttpsPortText), resetChanges);
        Assert.Equal("SMB (Named Pipe)", reset.ViewModel.ActiveTransportsText);
        Assert.Equal("None", reset.ViewModel.HttpsPortText);
    }

    [Fact]
    public async Task StartupToggleRefreshesTheAgentTabFromTheSameServiceSnapshot()
    {
        var context = CreateContext();
        context.Service.StartType = AgentServiceStartType.Manual;
        await context.ViewModel.RefreshAsync();

        context.ViewModel.StartsWithWindows = true;

        Assert.Equal([AgentServiceStartupPreference.Automatic], context.Service.StartupChanges);
        Assert.Equal("Automatic", context.ViewModel.ServiceStartTypeText);
        Assert.True(context.ViewModel.StartsWithWindows);
        Assert.True(context.Service.DescribeCalls >= 2);
    }

    [Fact]
    public async Task OpeningTheAgentTabRefreshesItsServiceSnapshot()
    {
        var context = CreateContext();
        await context.ViewModel.RefreshAsync();
        var readsBeforeNavigation = context.Service.DescribeCalls;

        context.ViewModel.HeaderActionCommand.Execute(null);
        context.Service.StartType = AgentServiceStartType.Manual;
        context.Service.Account = @"EXAMPLE\svc_nutmanager";
        context.ViewModel.SelectAgentTabCommand.Execute(null);

        Assert.True(context.Service.DescribeCalls > readsBeforeNavigation);
        Assert.Equal("Manual", context.ViewModel.ServiceStartTypeText);
        Assert.Equal(@"EXAMPLE\svc_nutmanager", context.ViewModel.ServiceAccountText);
    }

    // ---------------------------------------------------------------- T42: the listener, watched

    /// <summary>
    /// A running service is not a running listener, and the row says which one it is.
    ///
    /// This is the machine the composed answer got wrong: HTTPS enabled, the endpoint valid, the SSL
    /// binding, the URL reservation and the firewall rule all NutManager owned, the service
    /// comfortably Running - and nothing accepting connections, because HTTP.sys refused the prefix.
    /// The old row added those facts up and showed a green light. The three configuration rows are
    /// still correct and stay green; only the row that made a claim about the endpoint changes,
    /// because only that row was wrong.
    /// </summary>
    [Fact]
    public async Task TheListenerIsObservedRatherThanInferredFromEverythingAroundIt()
    {
        var context = ConfiguredContext(AgentServiceState.Running);
        context.Listener.Answer = AgentListenerObservation.Unreachable("ConnectionRefused: no listener.");
        await context.ViewModel.RefreshAsync();

        var status = context.ViewModel.ResourceStatus;
        Assert.Equal(4, status.Count);

        // Everything that is genuinely configured still reports as configured.
        Assert.Equal(AgentDiagnosticState.Ready, status[0].State);
        Assert.Equal(AgentDiagnosticState.Ready, status[1].State);
        Assert.Equal(AgentDiagnosticState.Ready, status[2].State);

        // And the one fact nobody verified is now the one fact somebody asked about.
        Assert.Equal(AgentDiagnosticState.Attention, status[3].State);
        Assert.Equal("Listener unavailable", status[3].Detail);
        Assert.Contains("nothing is listening", status[3].TechnicalDetail, StringComparison.Ordinal);
        Assert.Contains("ConnectionRefused", status[3].TechnicalDetail, StringComparison.Ordinal);

        // The service card is untouched by any of it: it reports the service, which is running.
        Assert.True(context.ViewModel.ServiceIsRunning);
    }

    /// <summary>An endpoint that answers is reported as listening, and names itself.</summary>
    [Fact]
    public async Task AnEndpointThatAnswersIsReportedActive()
    {
        var context = ConfiguredContext(AgentServiceState.Running);
        await context.ViewModel.RefreshAsync();

        var listener = context.ViewModel.ResourceStatus[3];
        Assert.Equal(AgentDiagnosticState.Ready, listener.State);
        Assert.Equal("Active", listener.Detail);
        Assert.Contains("https://nut-server.example.local:5199/", listener.TechnicalDetail, StringComparison.Ordinal);

        // Asked about the endpoint the rest of the strip describes, not one of its own.
        var target = Assert.Single(context.Listener.Targets);
        Assert.Equal("nut-server.example.local", target.Host);
        Assert.Equal(5199, target.Port);
    }

    /// <summary>
    /// The row follows the endpoint on its own, with nobody touching the window.
    ///
    /// Down, then up, then down again, across three periods of the clock - no navigation, no Refresh,
    /// and no reopening. This is the whole point of the monitor, so it is asserted as one sequence
    /// rather than three tests that each prove a single edge.
    /// </summary>
    [Fact]
    public async Task TheRowFollowsTheEndpointAcrossPeriodsWithoutAnybodyTouchingTheWindow()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        context.Listener.Answer = AgentListenerObservation.Unreachable("ConnectionRefused");
        await context.ViewModel.RefreshAsync();
        Assert.Equal(AgentDiagnosticState.Attention, context.ViewModel.ResourceStatus[3].State);

        context.ViewModel.StartListenerMonitor();

        // It comes up.
        context.Listener.Answer = AgentListenerObservation.Listening;
        var active = WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Ready);
        await clock.WaitForTimerCountAsync(1);
        clock.Tick();
        await active;

        // It goes away again.
        context.Listener.Answer = AgentListenerObservation.Unreachable("ConnectionRefused");
        var gone = WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Attention);
        await clock.WaitForTimerCountAsync(2);
        clock.Tick();
        await gone;

        // And it comes back.
        context.Listener.Answer = AgentListenerObservation.Listening;
        var back = WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Ready);
        await clock.WaitForTimerCountAsync(3);
        clock.Tick();
        await back;

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// With the transport off there is nothing on the network to ask about, and nothing is asked.
    ///
    /// Several periods, and the count stays at zero. A monitor that probed a disabled endpoint would
    /// be attempting a connection to whatever happens to be on that port on a machine whose
    /// administrator has deliberately turned the transport off.
    /// </summary>
    [Fact]
    public async Task DisabledHttpsIsNeverProbed()
    {
        var clock = new ManualClock();
        var context = CreateContext(clock: clock);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.StartListenerMonitor();

        for (var period = 1; period <= 3; period++)
        {
            await clock.WaitForTimerCountAsync(period);
            clock.Tick();
        }

        await clock.WaitForTimerCountAsync(4);
        Assert.Equal(0, context.Listener.Calls);
        Assert.Equal("HTTPS disabled", context.ViewModel.ResourceStatus[3].Detail);

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// A stopped service costs no network call.
    ///
    /// The row already names the reason - the service is stopped - and a connection attempt could add
    /// nothing to it. Asking anyway, once a second, for as long as the window is open, is the version
    /// of this feature that would have been rejected.
    /// </summary>
    [Fact]
    public async Task AStoppedServiceIsNeverProbed()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Stopped, clock);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.StartListenerMonitor();

        for (var period = 1; period <= 3; period++)
        {
            await clock.WaitForTimerCountAsync(period);
            clock.Tick();
        }

        await clock.WaitForTimerCountAsync(4);
        Assert.Equal(0, context.Listener.Calls);

        var listener = context.ViewModel.ResourceStatus[3];
        Assert.Equal(AgentDiagnosticState.Attention, listener.State);
        Assert.Equal("Listener unavailable", listener.Detail);
        Assert.Contains("stopped", listener.TechnicalDetail, StringComparison.OrdinalIgnoreCase);

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// Starting the service asks at once, and the green light waits for the answer.
    ///
    /// The order matters and is asserted as an order: the service reaches Running, the endpoint is
    /// asked, it is not ready yet and the row says so, and only a later successful answer turns it
    /// green. A screen that went green on the start succeeding would be reporting an intention.
    /// </summary>
    [Fact]
    public async Task StartingTheServiceAsksAtOnceAndWaitsForARealAnswer()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Stopped, clock);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.StartListenerMonitor();

        // The prefix is not open yet, which is the normal state of a service one moment old.
        context.Listener.Answer = AgentListenerObservation.Unreachable("ConnectionRefused");
        await context.ViewModel.StartServiceCommand.ExecuteAsync(null);

        Assert.True(context.ViewModel.ServiceIsRunning);

        // Asked immediately, without waiting for the period to elapse.
        await context.Listener.WaitForCallsAsync(1);
        await WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Attention);

        // And green only once the endpoint actually answers.
        context.Listener.Answer = AgentListenerObservation.Listening;
        var active = WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Ready);
        await clock.WaitForTimerCountAsync(2);
        clock.Tick();
        await active;

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// Stopping the service reports it at once, and asks nothing.
    ///
    /// The row does not wait for a period, and it does not attempt a connection to an endpoint whose
    /// service has just been stopped: the reason is known, and it is the one shown.
    /// </summary>
    [Fact]
    public async Task StoppingTheServiceReportsTheListenerAtOnce()
    {
        var context = ConfiguredContext(AgentServiceState.Running);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.StartListenerMonitor();
        Assert.Equal(AgentDiagnosticState.Ready, context.ViewModel.ResourceStatus[3].State);

        var probesBefore = context.Listener.Calls;
        await context.ViewModel.StopServiceCommand.ExecuteAsync(null);

        var listener = context.ViewModel.ResourceStatus[3];
        Assert.Equal(AgentDiagnosticState.Attention, listener.State);
        Assert.Equal("Listener unavailable", listener.Detail);
        Assert.Contains("stopped", listener.TechnicalDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(probesBefore, context.Listener.Calls);

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// A restart goes down and comes back, and the row goes with it.
    ///
    /// Restart leaves the service Running the moment it returns, so this is the case where reporting
    /// the service state as the listener state would be least visible and most wrong.
    /// </summary>
    [Fact]
    public async Task RestartingTheServiceGoesDownAndComesBack()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.StartListenerMonitor();
        Assert.Equal(AgentDiagnosticState.Ready, context.ViewModel.ResourceStatus[3].State);

        context.Listener.Answer = AgentListenerObservation.Unreachable("ConnectionRefused");
        var down = WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Attention);
        await context.ViewModel.RestartServiceCommand.ExecuteAsync(null);
        await down;

        Assert.True(context.ViewModel.ServiceIsRunning);

        context.Listener.Answer = AgentListenerObservation.Listening;
        var up = WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Ready);
        await clock.WaitForTimerCountAsync(2);
        clock.Tick();
        await up;

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// Applying refreshes the row and still does not restart anything.
    ///
    /// The product rule that Apply never restarts the agent is older than this feature and outranks
    /// it: what a saved configuration changes is what the listener will be after somebody restarts
    /// it, and until then the row keeps reporting the listener that is actually there.
    /// </summary>
    [Fact]
    public async Task ApplyingAsksAgainWithoutRestartingTheService()
    {
        var context = ConfiguredContext(AgentServiceState.Running);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.StartListenerMonitor();

        var probesBefore = context.Listener.Calls;
        context.ViewModel.HttpsPort = 5443;
        await context.ViewModel.ApplyCommand.ExecuteAsync(null);

        await context.Listener.WaitForCallsAsync(probesBefore + 1);
        Assert.Equal(0, context.Service.RestartCalls);
        Assert.Equal(0, context.Service.StartCalls);

        // The new endpoint is the one now being asked about.
        Assert.Equal(5443, context.Listener.Targets[^1].Port);

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// After a reset there is no endpoint left, so nothing is asked about one.
    ///
    /// Continuing to probe a removed binding would be a connection attempt against whatever inherits
    /// the port, once a second, for a transport the operator has just switched off.
    /// </summary>
    [Fact]
    public async Task ResettingHttpsStopsAskingAboutTheEndpoint()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.StartListenerMonitor();

        context.ViewModel.ResetHttpsCommand.Execute(null);
        await context.ViewModel.ConfirmCommand.ExecuteAsync(null);
        Assert.False(context.ViewModel.HttpsEnabled);

        var probesAfterReset = context.Listener.Calls;

        for (var period = 1; period <= 3; period++)
        {
            await clock.WaitForTimerCountAsync(period);
            clock.Tick();
        }

        await clock.WaitForTimerCountAsync(4);
        Assert.Equal(probesAfterReset, context.Listener.Calls);
        Assert.Equal("HTTPS disabled", context.ViewModel.ResourceStatus[3].Detail);

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// A slow endpoint is never asked twice at once.
    ///
    /// The tick that arrives while a probe is still waiting is dropped, not queued. Queueing is how a
    /// connection attempt that takes longer than the period turns one probe per second into a backlog
    /// that outlives the window.
    /// </summary>
    [Fact]
    public async Task ASlowEndpointIsNeverAskedTwiceAtOnce()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Listener.Held = held;

        var opening = context.ViewModel.RefreshAsync();
        await context.Listener.WaitForCallsAsync(1);

        context.ViewModel.StartListenerMonitor();

        // Three periods elapse while the first probe is still waiting for an answer.
        for (var period = 1; period <= 3; period++)
        {
            await clock.WaitForTimerCountAsync(period);
            clock.Tick();
        }

        await clock.WaitForTimerCountAsync(4);
        Assert.Equal(1, context.Listener.Calls);

        context.Listener.Held = null;
        held.SetResult();
        await opening;

        Assert.Equal(1, context.Listener.MaximumConcurrent);
        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// An answer that has not changed does not redraw anything.
    ///
    /// Twenty minutes of a healthy listener is over a thousand identical answers, and repopulating the
    /// strip for each of them would rebuild an observable collection once a second underneath the
    /// operator. Ten periods, one answer, and the collection is left alone.
    /// </summary>
    [Fact]
    public async Task AnUnchangedAnswerRedrawsNothing()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        await context.ViewModel.RefreshAsync();
        Assert.Equal(AgentDiagnosticState.Ready, context.ViewModel.ResourceStatus[3].State);

        var redraws = 0;
        context.ViewModel.ResourceStatus.CollectionChanged += (_, _) => redraws++;

        context.ViewModel.StartListenerMonitor();

        for (var period = 1; period <= 10; period++)
        {
            await clock.WaitForTimerCountAsync(period);
            clock.Tick();
            await context.Listener.WaitForCallsAsync(period + 1);
        }

        Assert.Equal(0, redraws);
        Assert.Equal(AgentDiagnosticState.Ready, context.ViewModel.ResourceStatus[3].State);

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// Closing the window while a probe is in flight updates nothing afterwards.
    ///
    /// The connection attempt cannot be recalled, so what matters is that its answer lands nowhere: a
    /// late write into a view model whose window is gone is the bug this asserts is absent.
    /// </summary>
    [Fact]
    public async Task ClosingTheWindowDuringAProbeUpdatesNothing()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        context.Listener.Answer = AgentListenerObservation.Unreachable("ConnectionRefused");
        await context.ViewModel.RefreshAsync();
        Assert.Equal(AgentDiagnosticState.Attention, context.ViewModel.ResourceStatus[3].State);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Listener.Held = held;

        // Opening the window left a request standing, so the monitor asks straight away - and the
        // endpoint takes its time answering.
        context.ViewModel.StartListenerMonitor();
        await context.Listener.WaitForCallsAsync(2);

        // The window closes with the answer still outstanding, and the answer then arrives.
        context.ViewModel.StopListenerMonitor();
        Assert.False(context.ViewModel.IsListenerMonitorRunning);

        context.Listener.Answer = AgentListenerObservation.Listening;
        held.SetResult();

        var probes = context.Listener.Calls;
        clock.Tick();

        // Nothing further was asked, and the answer that arrived late changed nothing on screen.
        Assert.Equal(probes, context.Listener.Calls);
        Assert.Equal(AgentDiagnosticState.Attention, context.ViewModel.ResourceStatus[3].State);
    }

    /// <summary>
    /// One failed probe is one failed probe, not the end of the monitor.
    ///
    /// An adapter is expected to translate its own failures, so a throw is unexpected by definition -
    /// and a loop that died on the first unexpected thing would leave a window whose listener row
    /// silently stopped being true.
    /// </summary>
    [Fact]
    public async Task AThrownProbeDoesNotEndTheLoop()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.StartListenerMonitor();

        context.Listener.Throws = new InvalidOperationException("the adapter fell over");
        var failed = WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Attention);
        await clock.WaitForTimerCountAsync(1);
        clock.Tick();
        await failed;

        Assert.Contains(
            "InvalidOperationException",
            context.ViewModel.ResourceStatus[3].TechnicalDetail,
            StringComparison.Ordinal);

        // And the next period asks again.
        context.Listener.Throws = null;
        var recovered = WaitForListenerAsync(context.ViewModel, AgentDiagnosticState.Ready);
        await clock.WaitForTimerCountAsync(2);
        clock.Tick();
        await recovered;

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// One window, one loop. Starting it again does not add a second one.
    ///
    /// Two loops on interleaved clocks would double the connection attempts and race each other to
    /// publish, which is the failure mode a window that starts its monitor from more than one place
    /// would produce. A period is one probe however many times Start was called.
    /// </summary>
    [Fact]
    public async Task StartingTheMonitorTwiceLeavesOneLoop()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        await context.ViewModel.RefreshAsync();

        var opening = context.Listener.Calls;

        context.ViewModel.StartListenerMonitor();
        context.ViewModel.StartListenerMonitor();
        context.ViewModel.StartListenerMonitor();
        Assert.True(context.ViewModel.IsListenerMonitorRunning);

        // Opening the window left a request standing, and it is served once however many times the
        // monitor was started. One timer then waits for the next period - not three.
        await context.Listener.WaitForCallsAsync(opening + 1);
        await clock.WaitForTimerCountAsync(1);
        Assert.Equal(1, clock.TimerCount);

        var served = context.Listener.Calls;
        clock.Tick();

        await context.Listener.WaitForCallsAsync(served + 1);
        await clock.WaitForTimerCountAsync(2);

        Assert.Equal(served + 1, context.Listener.Calls);
        Assert.Equal(2, clock.TimerCount);

        context.ViewModel.StopListenerMonitor();
        Assert.False(context.ViewModel.IsListenerMonitorRunning);
    }

    /// <summary>
    /// The monitor ends with the window, and schedules nothing after it.
    ///
    /// Asserted on the clock rather than on the loop: a timer scheduled after the window closed is a
    /// loop still running, whatever the loop believes about itself.
    /// </summary>
    [Fact]
    public async Task TheMonitorStopsWithTheWindow()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        await context.ViewModel.RefreshAsync();

        context.ViewModel.StartListenerMonitor();
        await clock.WaitForTimerCountAsync(1);

        context.ViewModel.StopListenerMonitor();
        Assert.False(context.ViewModel.IsListenerMonitorRunning);

        var scheduled = clock.TimerCount;
        var probes = context.Listener.Calls;

        clock.Tick();
        clock.Tick();

        Assert.Equal(scheduled, clock.TimerCount);
        Assert.Equal(probes, context.Listener.Calls);

        // Safe to call again on a window that is already closed.
        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>The row is written in the window language, in both of them.</summary>
    [Fact]
    public async Task TheListenerRowIsLocalized()
    {
        var portuguese = ConfiguredContext(AgentServiceState.Running, language: UiLanguagePreference.PtBr);
        portuguese.Listener.Answer = AgentListenerObservation.Unreachable("ConnectionRefused");
        await portuguese.ViewModel.RefreshAsync();

        var row = portuguese.ViewModel.ResourceStatus[3];
        Assert.Equal("Listener HTTPS", row.Label);
        Assert.Equal("Listener indisponível", row.Detail);
        Assert.Contains("nada está ouvindo", row.TechnicalDetail, StringComparison.Ordinal);

        var english = ConfiguredContext(AgentServiceState.Running);
        await english.ViewModel.RefreshAsync();
        Assert.Equal("HTTPS listener", english.ViewModel.ResourceStatus[3].Label);
        Assert.Equal("Active", english.ViewModel.ResourceStatus[3].Detail);
    }

    /// <summary>
    /// Coming back to the configuration surface asks again, so the row is current when it reappears.
    /// </summary>
    [Fact]
    public async Task ReturningToTheConfigurationSurfaceAsksAgain()
    {
        var context = ConfiguredContext(AgentServiceState.Running);
        await context.ViewModel.RefreshAsync();
        context.ViewModel.StartListenerMonitor();

        context.ViewModel.Surface = AgentConfigSurface.Settings;
        var probes = context.Listener.Calls;

        context.ViewModel.Surface = AgentConfigSurface.Configuration;
        await context.Listener.WaitForCallsAsync(probes + 1);

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// The periodic monitor is a listener monitor, and nothing more.
    ///
    /// This is the constraint that keeps a one-second cadence acceptable. The SSL binding, the URL
    /// reservation and the firewall rule are HTTP.sys and firewall queries; the service configuration
    /// is a service control manager call; the document is a file read. Repeating any of them every
    /// second, on every open window, is a machine-wide poll wearing a listener row as a disguise -
    /// so ten periods here move exactly one counter.
    /// </summary>
    [Fact]
    public async Task ThePeriodicMonitorAsksNothingButTheEndpoint()
    {
        var clock = new ManualClock();
        var context = ConfiguredContext(AgentServiceState.Running, clock);
        await context.ViewModel.RefreshAsync();

        var reads = context.Store.Reads;
        var resources = context.Resources.DescribeCalls;
        var certificates = context.Certificates.ListCalls;
        var service = context.Service.DescribeCalls;
        var groups = context.Groups.DescribeCalls;

        context.ViewModel.StartListenerMonitor();
        await context.Listener.WaitForCallsAsync(2);

        for (var period = 1; period <= 10; period++)
        {
            await clock.WaitForTimerCountAsync(period);
            clock.Tick();
            await context.Listener.WaitForCallsAsync(period + 2);
        }

        // Eleven observations of the endpoint.
        Assert.True(context.Listener.Calls >= 11, $"Expected the endpoint to be asked, saw {context.Listener.Calls}.");

        // And not one query of anything else.
        Assert.Equal(reads, context.Store.Reads);
        Assert.Equal(resources, context.Resources.DescribeCalls);
        Assert.Equal(certificates, context.Certificates.ListCalls);
        Assert.Equal(service, context.Service.DescribeCalls);
        Assert.Equal(groups, context.Groups.DescribeCalls);

        context.ViewModel.StopListenerMonitor();
    }

    /// <summary>
    /// A window with everything HTTPS needs: enabled, valid, every resource owned by NutManager, and
    /// a service in whichever state the case under test requires.
    /// </summary>
    private static TestContext ConfiguredContext(
        AgentServiceState serviceState,
        TimeProvider? clock = null,
        UiLanguagePreference? language = UiLanguagePreference.EnUs)
    {
        var context = CreateContext(
            document: HttpsDocument(), serviceState: serviceState, clock: clock, language: language);

        context.Resources.Snapshot = new AgentHttpsResourceSnapshot(
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager),
            new AgentResourceState(AgentResourceOwnership.OwnedByNutManager));

        return context;
    }

    /// <summary>
    /// Waits for the listener row to reach a state, driven by the collection rather than by the clock.
    ///
    /// The monitor publishes from a thread pool thread, so a test that asserted immediately after
    /// ticking would be asserting on a race. Waiting on the redraw itself keeps these tests off the
    /// wall clock; the timeout exists only so a broken monitor fails the test rather than hanging it.
    /// </summary>
    private static async Task WaitForListenerAsync(AgentConfigViewModel viewModel, AgentDiagnosticState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        bool Matches() =>
            viewModel.ResourceStatus.Count == 4 && viewModel.ResourceStatus[3].State == state;

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (Matches()) reached.TrySetResult();
        }

        viewModel.ResourceStatus.CollectionChanged += OnChanged;

        try
        {
            if (Matches()) return;

            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            viewModel.ResourceStatus.CollectionChanged -= OnChanged;
        }
    }

    private static void EnableValidHttps(AgentConfigViewModel viewModel)
    {
        viewModel.HttpsEnabled = true;
        viewModel.HttpsHost = "nut-server.example.local";
        viewModel.HttpsPort = 5199;
        viewModel.SelectedCertificate = Assert.Single(viewModel.Certificates);
        Assert.True(viewModel.HttpsIsValid, viewModel.HttpsValidationMessage);
    }

    private static AgentTransportConfigurationDocument HttpsDocument() => new()
    {
        NamedPipeEnabled = true,
        HttpsEnabled = true,
        HttpsPrefix = "https://nut-server.example.local:5199/",
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
        FakeListener Listener,
        List<string> Events);

    private sealed class FakeStore(AgentTransportConfigurationDocument document, List<string> events)
        : IAgentConfigurationStore
    {
        public List<AgentTransportConfigurationDocument> Writes { get; } = [];
        public AgentConfigurationWriteResult WriteResult { get; set; } = AgentConfigurationWriteResult.Success;
        public string Path => "agent.json";
        public bool Exists => true;

        public int Reads { get; private set; }

        public AgentTransportConfigurationDocument Read()
        {
            Reads++;
            return document;
        }

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
            new(AgentMembershipOutcome.Added, @"EXAMPLE\operator");

        public int DescribeCalls { get; private set; }

        public AgentOperatorsGroupState Describe()
        {
            DescribeCalls++;
            return new(
                exists || CreateCalls > 0,
                "NutManager Operators",
                CreateCalls > 0 || exists ? "S-1-5-32-1000" : null,
                role,
                null);
        }

        public AgentGroupCreationResult Create()
        {
            CreateCalls++;
            return new AgentGroupCreationResult(true, "NutManager Operators", "S-1-5-32-1000", null);
        }

        public AgentIdentityResolution ResolveIdentity(string accountName) =>
            new(true, accountName, "S-1-5-21-1000", AgentPrincipalKind.User, "EXAMPLE", null);

        public AgentMembershipResult AddMember(string accountName)
        {
            AddCalls++;
            return AddResult with { AccountName = accountName };
        }

        public IReadOnlyList<string> ListMembers() => exists ? [@"EXAMPLE\operator"] : [];
    }

    private sealed class FakeService(AgentServiceState state) : IAgentServiceAdministration
    {
        /// <summary>
        /// What the service control manager would say now.
        ///
        /// Settable, and moved by the operations below, because a fake that answers Stopped after a
        /// successful start cannot exercise the rule that matters: a service reaching Running is not
        /// the same event as its listener opening.
        /// </summary>
        public AgentServiceState State { get; set; } = state;

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int RestartCalls { get; private set; }
        public int DescribeCalls { get; private set; }

        /// <summary>Every start-type change asked for, so a test can prove which one and how many.</summary>
        public List<AgentServiceStartupPreference> StartupChanges { get; } = [];

        /// <summary>Set to make the service control manager refuse the change.</summary>
        public string? StartupFailure { get; set; }

        public AgentServiceStartType StartType { get; set; } = AgentServiceStartType.Automatic;

        public string Account { get; set; } = "LocalSystem";
        public string? Failure { get; set; }
        public int? QueryErrorCode { get; set; }

        public AgentServiceSnapshot Describe()
        {
            DescribeCalls++;
            return new(State, StartType.ToString(), Failure, StartType, Account, QueryErrorCode);
        }

        public Task<AgentServiceOutcome> SetStartupAsync(
            AgentServiceStartupPreference preference, CancellationToken cancellationToken)
        {
            StartupChanges.Add(preference);

            if (StartupFailure is not null)
            {
                return Task.FromResult(new AgentServiceOutcome(false, State, StartupFailure));
            }

            StartType = preference is AgentServiceStartupPreference.Automatic
                ? AgentServiceStartType.Automatic
                : AgentServiceStartType.Manual;

            return Task.FromResult(new AgentServiceOutcome(true, State, null));
        }

        public Task<AgentServiceOutcome> StartAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            State = AgentServiceState.Running;
            return Task.FromResult(new AgentServiceOutcome(true, AgentServiceState.Running, null));
        }

        public Task<AgentServiceOutcome> StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            State = AgentServiceState.Stopped;
            return Task.FromResult(new AgentServiceOutcome(true, AgentServiceState.Stopped, null));
        }

        public Task<AgentServiceOutcome> RestartAsync(CancellationToken cancellationToken)
        {
            RestartCalls++;
            State = AgentServiceState.Running;
            return Task.FromResult(new AgentServiceOutcome(true, AgentServiceState.Running, null));
        }
    }

    private sealed class FakeResources(List<string> events) : IAgentHttpsResourceAdministration
    {
        public AgentHttpsResourceSnapshot Snapshot { get; set; } = AgentHttpsResourceSnapshot.None;
        public AgentHttpsResourceResult ApplyResult { get; set; } = AgentHttpsResourceResult.Success([]);
        public int ApplyCalls { get; private set; }
        public List<AgentHttpsCleanupRequest> RemoveRequests { get; } = [];

        public int DescribeCalls { get; private set; }

        public AgentHttpsResourceSnapshot Describe(AgentHttpsBinding binding)
        {
            DescribeCalls++;
            return Snapshot;
        }

        public AgentHttpsResourceResult Apply(AgentHttpsBinding binding)
        {
            ApplyCalls++;
            events.Add("resources.apply");
            return ApplyResult;
        }

        /// <summary>What Remove answers. Settable so a partial failure can be exercised.</summary>
        public AgentHttpsResourceResult RemoveResult { get; set; } = AgentHttpsResourceResult.Success([]);

        public AgentHttpsResourceResult Remove(AgentHttpsBinding binding, AgentHttpsCleanupRequest request)
        {
            RemoveRequests.Add(request);
            events.Add("resources.remove");
            return RemoveResult;
        }
    }

    private sealed class FakeCertificates(params AgentCertificateSummary[] certificates) : IAgentCertificateCatalog
    {
        private readonly List<AgentCertificateSummary> _certificates = [.. certificates];

        public int ListCalls { get; private set; }

        public IReadOnlyList<AgentCertificateSummary> List()
        {
            ListCalls++;
            return _certificates;
        }

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
        /// <summary>Proves that choosing an installed certificate never imports anything.</summary>
        public int ImportCalls { get; private set; }

        public AgentCertificateImportResult Result { get; set; } =
            AgentCertificateImportResult.From(AgentCertificateImportOutcome.Failed);

        /// <summary>Thrown instead of returning, to exercise the unanticipated-failure boundary.</summary>
        public Exception? Failure { get; set; }

        /// <summary>Recorded only so a test can prove the password never reaches view state.</summary>
        public string? LastPassword { get; private set; }

        public AgentCertificateImportResult Import(string path, string? password)
        {
            ImportCalls++;
            LastPassword = password;
            if (Failure is not null) throw Failure;
            if (Result.Certificate is { } certificate) certificates.Add(certificate);
            return Result;
        }
    }

    /// <summary>
    /// A clock whose timers only fire when a test says so.
    ///
    /// Hand-written rather than pulled from a package: one feature needs a controllable delay, and a
    /// new dependency for fifteen lines would be the worse trade. Timers are kept in creation order
    /// so a test can fire the stale one and prove it does nothing.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TimerCount
        {
            get
            {
                lock (_gate) return _timers.Count;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);

            lock (_gate)
            {
                _timers.Add(timer);
                _scheduled.TrySetResult();
                _scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return timer;
        }

        /// <summary>Fires one timer by creation order, whether or not it still matters.</summary>
        public void Fire(int index)
        {
            ManualTimer timer;
            lock (_gate) timer = _timers[index];
            timer.Fire();
        }

        /// <summary>
        /// Fires every timer that has been scheduled and not yet fired.
        ///
        /// This is one tick of the polling period for anything waiting on the clock. It is separate
        /// from <see cref="Fire(int)"/> because that one deliberately re-fires a stale timer to prove
        /// it does nothing, and a period is the opposite: everything currently waiting, once.
        /// </summary>
        public void Tick()
        {
            List<ManualTimer> due;
            lock (_gate) due = _timers.Where(timer => timer.IsPending).ToList();
            foreach (var timer in due) timer.Fire();
        }

        /// <summary>
        /// Completes once at least this many timers have been scheduled.
        ///
        /// A loop that waits on the clock schedules its timer from a thread pool thread, so a test
        /// that ticked immediately would usually tick nothing. Waiting on the schedule rather than on
        /// elapsed milliseconds is what keeps these tests off the wall clock.
        /// </summary>
        public async Task WaitForTimerCountAsync(int expected)
        {
            while (true)
            {
                Task wait;

                lock (_gate)
                {
                    if (_timers.Count >= expected) return;
                    wait = _scheduled.Task;
                }

                await wait.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            private bool _fired;

            /// <summary>Still waiting: neither fired nor abandoned.</summary>
            public bool IsPending => !_disposed && !_fired;

            public void Fire()
            {
                if (_disposed) return;

                _fired = true;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// An endpoint that answers what a test tells it to, when a test lets it.
    ///
    /// It counts, so a test can prove that a stopped service and a disabled transport cost no network
    /// call at all; it records the endpoint it was handed, so a test can prove which one is being
    /// asked about; and it tracks how many probes were in flight at once, because one at a time is a
    /// rule rather than an accident of how fast the fake answers.
    /// </summary>
    private sealed class FakeListener : IAgentHttpsListenerProbe
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;

        public int Calls { get; private set; }

        public int MaximumConcurrent { get; private set; }

        public List<AgentHttpsBinding> Targets { get; } = [];

        public AgentListenerObservation Answer { get; set; } = AgentListenerObservation.Listening;

        /// <summary>Set to hold a probe open until the test releases it.</summary>
        public TaskCompletionSource? Held { get; set; }

        /// <summary>Set to make the adapter itself fail, which the loop has to survive.</summary>
        public Exception? Throws { get; set; }

        public async Task<AgentListenerObservation> ProbeAsync(
            AgentHttpsBinding binding, CancellationToken cancellationToken)
        {
            TaskCompletionSource? held;

            lock (_gate)
            {
                Calls++;
                Targets.Add(binding);
                _active++;
                MaximumConcurrent = Math.Max(MaximumConcurrent, _active);
                _called.TrySetResult();
                _called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                held = Held;
            }

            try
            {
                if (held is not null) await held.Task.WaitAsync(cancellationToken);
                if (Throws is { } failure) throw failure;

                return Answer;
            }
            finally
            {
                lock (_gate) _active--;
            }
        }

        /// <summary>Completes once the endpoint has been asked at least this many times.</summary>
        public async Task WaitForCallsAsync(int expected)
        {
            while (true)
            {
                Task wait;

                lock (_gate)
                {
                    if (Calls >= expected) return;
                    wait = _called.Task;
                }

                await wait.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    /// <summary>An in-memory preference store, so no test reads or writes a real user profile.</summary>
    private sealed class FakePreferences(
        UiLanguagePreference? saved = null, ThemePreference? savedTheme = null) : IAgentConfigUiPreferences
    {
        public UiLanguagePreference? Saved { get; private set; } = saved;

        public int Writes { get; private set; }

        public ThemePreference? SavedTheme { get; private set; } = savedTheme;

        public int ThemeWrites { get; private set; }

        public UiLanguagePreference? ReadLanguage() => Saved;

        public void WriteLanguage(UiLanguagePreference language)
        {
            Saved = language;
            Writes++;
        }

        public ThemePreference? ReadTheme() => SavedTheme;

        public void WriteTheme(ThemePreference theme)
        {
            SavedTheme = theme;
            ThemeWrites++;
        }
    }

    private sealed class FakeInventory(string? dotNet = "10.0.0", string? aspNetCore = "10.0.0")
        : IAgentRuntimeInventory
    {
        public Task<AgentRuntimeInventorySnapshot> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRuntimeInventorySnapshot(dotNet, aspNetCore, true, true, "NUT"));
    }
}
