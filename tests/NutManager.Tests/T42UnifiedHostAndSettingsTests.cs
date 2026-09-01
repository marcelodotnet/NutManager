using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using NutManager.Agent;
using NutManager.Agent.Config.Localization;
using NutManager.Agent.Config.ViewModels;
using NutManager.Core.Agent;
using NutManager.Infrastructure.AgentConfiguration;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// Which mode the one executable runs in.
///
/// This is the whole security surface of the unification: one process image that is either a Windows
/// service or an elevated administrative window, decided entirely by what is on the command line. The
/// resolver is pure so the decision can be tested without a service control manager, a window station
/// or a process, and every case below is a case an operator or the SCM can actually produce.
/// </summary>
public sealed class T42AgentExecutionModeTests
{
    [Theory]
    [InlineData("--service")]
    [InlineData("--SERVICE")]
    [InlineData("--Service")]
    public void TheServiceSwitchSelectsTheServiceWhoeverStartedIt(string argument)
    {
        // Case-insensitive because the SCM stores the command line as it was registered and an
        // operator repairing a service by hand will not match the installer's casing.
        Assert.Equal(AgentExecutionMode.Service, AgentExecutionModeResolver.Resolve([argument], false));
        Assert.Equal(AgentExecutionMode.Service, AgentExecutionModeResolver.Resolve([argument], true));
    }

    [Theory]
    [InlineData("--config")]
    [InlineData("--CONFIG")]
    public void TheConfigSwitchSelectsTheWindowWhoeverStartedIt(string argument)
    {
        Assert.Equal(AgentExecutionMode.Config, AgentExecutionModeResolver.Resolve([argument], false));
        Assert.Equal(AgentExecutionMode.Config, AgentExecutionModeResolver.Resolve([argument], true));
    }

    /// <summary>
    /// No arguments is the compatibility case, and it is the only case that consults context.
    ///
    /// A service registered by an earlier version has no --service in its stored command line, and the
    /// SCM will keep starting it exactly as recorded. Resolving by context is what stops an upgrade
    /// from turning an installed service into a process that tries to open a window in session 0.
    /// </summary>
    [Fact]
    public void NoArgumentsFollowsWhoeverStartedTheProcess()
    {
        Assert.Equal(AgentExecutionMode.Service, AgentExecutionModeResolver.Resolve([], true));
        Assert.Equal(AgentExecutionMode.Config, AgentExecutionModeResolver.Resolve([], false));
    }

    /// <summary>
    /// Anything else fails closed.
    ///
    /// No argument names a path, a command or an executable, and none is passed through to anything.
    /// An unrecognised command line is refused rather than interpreted, so there is no input by which
    /// this process can be made to run something that is not one of its own two modes.
    /// </summary>
    [Theory]
    [InlineData("--serv")]
    [InlineData("-service")]
    [InlineData("/service")]
    [InlineData("--service=1")]
    [InlineData("")]
    [InlineData("cmd.exe")]
    [InlineData("--config;cmd.exe")]
    public void AnUnrecognisedArgumentIsRefused(string argument)
    {
        Assert.Equal(AgentExecutionMode.Invalid, AgentExecutionModeResolver.Resolve([argument], false));
        Assert.Equal(AgentExecutionMode.Invalid, AgentExecutionModeResolver.Resolve([argument], true));
    }

    /// <summary>
    /// One argument, never two. A second argument is refused rather than ignored, so nothing can be
    /// smuggled in behind a valid switch.
    /// </summary>
    [Fact]
    public void MoreThanOneArgumentIsRefused()
    {
        Assert.Equal(
            AgentExecutionMode.Invalid,
            AgentExecutionModeResolver.Resolve(["--config", "--service"], false));

        Assert.Equal(
            AgentExecutionMode.Invalid,
            AgentExecutionModeResolver.Resolve(["--service", "cmd.exe"], true));

        Assert.Equal(
            AgentExecutionMode.Invalid,
            AgentExecutionModeResolver.Resolve(["--config", "https://example.invalid"], false));
    }

    [Fact]
    public void TheResolverRefusesANullArgumentList() =>
        Assert.Throws<ArgumentNullException>(() => AgentExecutionModeResolver.Resolve(null!, false));
}

/// <summary>
/// One executable, two modes, and never both at once.
///
/// Asserted against the sources rather than by starting the process: a test that launched the service
/// mode would need a service control manager, and one that launched the window would need a desktop
/// and an elevation prompt. What has to hold is structural, and structure is what these read.
/// </summary>
public sealed class T42UnifiedHostTests
{
    [Fact]
    public void TheProductBuildsOneAgentExecutable()
    {
        var host = Read("src/NutManager.Agent/NutManager.Agent.csproj");
        var window = Read("src/NutManager.Agent.Config/NutManager.Agent.Config.csproj");

        // WinExe, so launching the window by hand does not flash a console behind it. A service needs
        // no console, so nothing is lost by the choice.
        Assert.Contains("<OutputType>WinExe</OutputType>", host, StringComparison.Ordinal);
        Assert.Contains("<OutputType>Library</OutputType>", window, StringComparison.Ordinal);

        // The dependency runs one way. The host may reference the window module; the module must not
        // reference the host, and neither may reference the desktop application.
        Assert.Contains("NutManager.Agent.Config.csproj", host, StringComparison.Ordinal);
        Assert.DoesNotContain("NutManager.Agent.csproj", window, StringComparison.Ordinal);
        Assert.DoesNotContain("NutManager.App.csproj", host, StringComparison.Ordinal);
        Assert.DoesNotContain("NutManager.App.csproj", window, StringComparison.Ordinal);

        // The manifest is a property of an executable, and there is exactly one.
        Assert.Contains("app.manifest", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicationManifest", window, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), "src/NutManager.Agent/app.manifest")));
        Assert.False(File.Exists(Path.Combine(RepositoryRoot(), "src/NutManager.Agent.Config/app.manifest")));
    }

    /// <summary>
    /// The service branch and the window branch are separate statements, and the service branch never
    /// mentions the UI.
    ///
    /// A single process that started a service host and an Avalonia lifetime together would be a
    /// service trying to draw on a desktop that does not exist in session 0. The dispatcher picks one.
    /// </summary>
    [Fact]
    public void TheHostNeverStartsAServiceAndAWindowInOneProcess()
    {
        // Comments stripped first: this file explains at length why the two modes never share a
        // process, and the word Avalonia appearing in that explanation is not the UI being started.
        var program = WithoutCodeComments(Read("src/NutManager.Agent/Program.cs"));

        Assert.Contains("ServiceBase.Run", program, StringComparison.Ordinal);
        Assert.Contains("AgentConfigHost.Run", program, StringComparison.Ordinal);

        // Each mode is reached from exactly one place.
        Assert.Single(Regex.Matches(program, Regex.Escape("ServiceBase.Run")));
        Assert.Single(Regex.Matches(program, Regex.Escape("AgentConfigHost.Run")));

        // The dispatcher never composes them.
        Assert.DoesNotContain("StartWithClassicDesktopLifetime", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", program, StringComparison.Ordinal);

        // A refused command line exits with a code rather than falling through to either mode.
        Assert.Contains("ExitInvalidArguments", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The installer starts the one executable in the right mode from both places it is started, and
    /// no longer installs a second one.
    /// </summary>
    [Fact]
    public void TheInstallerRegistersTheServiceAndTheShortcutAgainstTheSameFile()
    {
        var package = WithoutComments(Read("installer/Agent/Package.wxs"));

        Assert.Contains("Arguments=\"--service\"", package, StringComparison.Ordinal);
        Assert.Contains("Target=\"[#AgentExecutableFile]\"", package, StringComparison.Ordinal);
        Assert.Contains("Arguments=\"--config\"", package, StringComparison.Ordinal);

        Assert.DoesNotContain("NutManager.Agent.Config.exe", package, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentConfigExecutableFile", package, StringComparison.Ordinal);

        // The retired file goes away through the ordinary major-upgrade path. RemoveFile would delete
        // by name rather than by ownership, which is the one thing uninstall must never do.
        Assert.DoesNotContain("RemoveFile", package, StringComparison.Ordinal);
    }

    private static string WithoutComments(string source) =>
        Regex.Replace(source, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    /// <summary>Line and block comments removed, so a guard reads the code rather than the prose.</summary>
    internal static string WithoutCodeComments(string source) =>
        Regex.Replace(
            Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            "//.*",
            string.Empty);

    internal static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NutManager.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>
/// The settings surface: what it holds, and what it must never hold.
/// </summary>
public sealed class T42SettingsSurfaceTests
{
    /// <summary>
    /// One header button with two jobs: it opens settings, or it goes home.
    ///
    /// The glyph is the action rather than the location. A gear that did not open settings would be
    /// a lie about what pressing it does, so from settings and from the terms - where pressing it
    /// returns to the configuration surface - it is a house instead.
    /// </summary>
    [Fact]
    public void TheHeaderButtonOffersSettingsOrHome()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var viewModel = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs");

        var header = window.IndexOf("x:Name=\"AgentMainHeader\"", StringComparison.Ordinal);
        var surface = window.IndexOf("x:Name=\"ConfigurationSurface\"", header, StringComparison.Ordinal);
        var markup = window[header..surface];

        var button = markup.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal);
        var diagnostics = markup.IndexOf("Command=\"{Binding ToggleDiagnosticsCommand}\"", StringComparison.Ordinal);

        Assert.True(button >= 0, "The header must carry the settings button.");
        Assert.True(diagnostics > button, "It sits to the left of Diagnostics.");

        Assert.Contains("Command=\"{Binding HeaderActionCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Classes=\"agent-settings-button\"", markup, StringComparison.Ordinal);
        Assert.Contains("Button.agent-settings-button", window, StringComparison.Ordinal);

        // Two glyphs, one shown at a time, so the rotation class can belong to the gear alone.
        Assert.Contains("IsVisible=\"{Binding ShowSettingsAction}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowHomeAction}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Data=\"{DynamicResource AgentIconHome}\"", markup, StringComparison.Ordinal);

        // Home goes to the configuration surface; the gear opens settings. One command, two answers.
        Assert.Contains("public bool ShowHomeAction => ShowSettings || ShowTerms;", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "Surface = ShowHomeAction ? AgentConfigSurface.Configuration : AgentConfigSurface.Settings;",
            viewModel,
            StringComparison.Ordinal);

        // Navigation only: going home never cancels, discards or re-reads anything.
        var command = viewModel[viewModel.IndexOf("private void HeaderAction()", StringComparison.Ordinal)..];
        command = command[..command.IndexOf(';', StringComparison.Ordinal)];
        foreach (var forbidden in new[] { "Cancel", "Discard", "Reset", "Reload", "Refresh" })
        {
            Assert.DoesNotContain(forbidden, command, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Four destinations in one strip, drawn the way the desktop draws its administration sections:
    /// items sharing a surface, separated by hairlines, the current one carrying an accent line along
    /// its own bottom edge.
    /// </summary>
    [Fact]
    public void TheSettingsSurfaceHasFourSectionsSeparatedByDividers()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        Assert.Contains("IsVisible=\"{Binding ShowSettings}\"", window, StringComparison.Ordinal);

        foreach (var tab in new[] { "General", "Appearance", "Agent", "About" })
        {
            Assert.Contains($"Text=\"{{Binding Strings[Settings.Tab.{tab}]}}\"", window, StringComparison.Ordinal);
        }

        // Four items, four glyphs, and three hairlines between them - not four either side.
        Assert.Equal(4, Regex.Matches(window, "Classes=\"agent-section-tab\"").Count);
        Assert.Equal(4, Regex.Matches(window, "Classes=\"agent-tab-icon\"").Count);
        Assert.Equal(3, Regex.Matches(window, "Classes=\"agent-section-divider\"").Count);

        // The strip lives directly on the settings card. A TabControl would bring its own strip, and
        // a second card would make the navigation look like a panel of its own.
        Assert.DoesNotContain("<TabControl", window, StringComparison.Ordinal);
        Assert.DoesNotContain("<TabItem", window, StringComparison.Ordinal);

        var strip = window[window.IndexOf("x:Name=\"SettingsTabs\"", StringComparison.Ordinal)..];
        strip = strip[..strip.IndexOf("Settings.Tab.About", StringComparison.Ordinal)];
        Assert.DoesNotContain("agent-shell-card", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("nut-card", strip, StringComparison.Ordinal);

        // Each item is as wide as its own label: no fixed widths, no equal columns.
        Assert.DoesNotContain("Width=\"", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("<UniformGrid", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions", strip, StringComparison.Ordinal);

        // Compact, and still clearly under the card title rather than over it.
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"17\" />", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// The current section is marked by an accent line along the bottom of its own button, with the
    /// same low-contrast sheen behind it that the desktop uses.
    ///
    /// The sheen on its own was the whole marker for one round, and it made each item read as a small
    /// filled card. The desktop uses both together, and the line is what actually says "this one".
    /// </summary>
    [Fact]
    public void TheCurrentSectionIsMarkedByAnAccentLineOnItsOwnButton()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var shell = T42UnifiedHostTests.Read("src/NutManager.App/Presentation/Themes/NutShellStyles.axaml");

        // The line is a bottom border on the item, so it can only ever be as wide as the item.
        Assert.Contains(
            "<Setter Property=\"BorderThickness\" Value=\"0,0,0,2\" />",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"BorderThickness\" Value=\"0,0,0,2\" />",
            shell,
            StringComparison.Ordinal);

        var selected = window[window.IndexOf(
            "Button.agent-section-tab.selected /template/ ContentPresenter", StringComparison.Ordinal)..];
        selected = selected[..selected.IndexOf("</Style>", StringComparison.Ordinal)];
        Assert.Contains("NutAccentBrush", selected, StringComparison.Ordinal);
        Assert.Contains("NutSelectedSheenBrush", selected, StringComparison.Ordinal);

        // Accent, never a semantic colour.
        Assert.DoesNotContain("NutCriticalBrush", selected, StringComparison.Ordinal);
        Assert.DoesNotContain("NutWarningBrush", selected, StringComparison.Ordinal);

        // The desktop values, mirrored: padding, radius, and both brush transitions.
        foreach (var expected in new[]
                 {
                     "<Setter Property=\"Padding\" Value=\"13,9\" />",
                     "<Setter Property=\"CornerRadius\" Value=\"14\" />",
                     "<BrushTransition Property=\"Background\" Duration=\"0:0:0.14\" />",
                     "<BrushTransition Property=\"BorderBrush\" Duration=\"0:0:0.18\" />",
                     "NutGlassRowHoverBrush",
                 })
        {
            Assert.Contains(expected, shell, StringComparison.Ordinal);
            Assert.Contains(expected, window, StringComparison.Ordinal);
        }

        // The glyph of the current item takes the accent, as it does in the shell.
        Assert.Contains("Button.agent-section-tab.selected PathIcon", window, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontWeight\" Value=\"SemiBold\" />", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three surfaces are one value, and each is shown by its own flag. Two booleans could already
    /// describe "both" and "neither"; three would describe four impossible states.
    /// </summary>
    [Fact]
    public void EachSurfaceIsShownByItsOwnFlagOverOneValue()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var viewModel = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs");

        Assert.Contains("IsVisible=\"{Binding ShowConfiguration}\"", window, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowDiagnostics}\"", window, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowSettings}\"", window, StringComparison.Ordinal);

        Assert.Contains("private AgentConfigSurface _surface", viewModel, StringComparison.Ordinal);
        Assert.Contains("Surface == AgentConfigSurface.Configuration", viewModel, StringComparison.Ordinal);
        Assert.Contains("Surface == AgentConfigSurface.Diagnostics", viewModel, StringComparison.Ordinal);
        Assert.Contains("Surface == AgentConfigSurface.Settings", viewModel, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Agent panel reports and does not act. Every binding on it is a read, and no command,
    /// button or editable field appears in it.
    /// </summary>
    [Fact]
    public void TheAgentTabIsReadOnly()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var start = window.IndexOf("IsVisible=\"{Binding IsAgentTab}\"", StringComparison.Ordinal);
        var end = window.IndexOf("IsVisible=\"{Binding IsAboutTab}\"", start, StringComparison.Ordinal);
        var tab = window[start..end];

        foreach (var reported in new[]
                 {
                     "ServiceStateText",
                     "ServiceStartTypeText",
                     "ServiceAccountText",
                     "ActiveTransportsText",
                     "HttpsPortText",
                 })
        {
            Assert.Contains(reported, tab, StringComparison.Ordinal);
        }

        // The reported values stay reported. Nothing on this tab edits a start type, an account or a
        // port: those belong to the machine and to the configuration surface respectively.
        Assert.DoesNotContain("<TextBox", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBox", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("<ToggleSwitch", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("<CheckBox", tab, StringComparison.Ordinal);

        // Exactly one action, and it is the registration. A second button here would mean something
        // else on this tab had started changing the machine.
        Assert.Single(Regex.Matches(tab, "<Button"));
        Assert.Single(Regex.Matches(tab, "Command="));
        Assert.Contains("Command=\"{Binding InstallServiceCommand}\"", tab, StringComparison.Ordinal);
    }

    /// <summary>
    /// About states what this build is and links to one fixed page. No logo and no licence heading:
    /// neither was asked for, and both were explicitly excluded.
    /// </summary>
    [Fact]
    public void AboutReportsTheBuildAndLinksToOneFixedPage()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var about = window[window.IndexOf("IsVisible=\"{Binding IsAboutTab}\"", StringComparison.Ordinal)..];

        foreach (var reported in new[]
                 {
                     "AboutVersion",
                     "AboutBuild",
                     "AboutDotNetRuntime",
                     "AboutAspNetCoreRuntime",
                     "AboutDeveloper",
                     "AboutProjectPageUrl",
                 })
        {
            Assert.Contains(reported, about, StringComparison.Ordinal);
        }

        // The product names itself before listing what it is made of.
        Assert.Contains("Strings[About.Product]", about, StringComparison.Ordinal);

        Assert.Contains("Strings[About.Terms]", about, StringComparison.Ordinal);
        Assert.DoesNotContain("<Image", about, StringComparison.Ordinal);
        Assert.DoesNotContain("Licen", about, StringComparison.Ordinal);

        // The address is the link: one control, in the desktop application textual link style, rather
        // than an address to read and a separate button to press.
        Assert.Contains("Classes=\"nut-link\"", about, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding AboutProjectPageUrl}\"", about, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Content=\"{Binding Strings[About.ProjectPage.Open]}\"",
            about,
            StringComparison.Ordinal);

        // The command takes nothing. There is no text box, and no parameter, by which a target could
        // be supplied from the window.
        Assert.Contains("Command=\"{Binding OpenProjectPageCommand}\"", about, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter", about, StringComparison.Ordinal);
        Assert.DoesNotContain("<TextBox", about, StringComparison.Ordinal);

        // The terms are reached from here, and the long legal text no longer sits inline.
        Assert.Contains("Command=\"{Binding OpenTermsCommand}\"", about, StringComparison.Ordinal);
        Assert.Contains("Strings[About.Terms.View]", about, StringComparison.Ordinal);
        Assert.DoesNotContain("About.Terms.Text", window, StringComparison.Ordinal);
    }
}

/// <summary>
/// The project-page link, which is the only address this product opens.
/// </summary>
public sealed class T42ProjectPageLauncherTests
{
    [Fact]
    public void TheLauncherOpensOneCompiledInAddressAndTakesNoUrl()
    {
        var contract = T42UnifiedHostTests.Read("src/NutManager.Core/Agent/AgentAdministrationContracts.cs");
        var launcher = T42UnifiedHostTests.Read(
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentProjectPageLauncher.cs");

        // No method here accepts a URL, so there is no generic way to make this process open a target.
        Assert.Contains("bool OpenProjectPage();", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenProjectPage(string", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenUrl", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenUrl", launcher, StringComparison.Ordinal);

        // The address is a constant, and it is the project's own.
        Assert.Contains(
            "private const string ProjectPage = \"https://github.com/marcelodotnet/NutManager\";",
            launcher,
            StringComparison.Ordinal);

        // ShellExecute resolves the https handler. It is not a shell, and no named interpreter appears.
        Assert.Contains("UseShellExecute = true", launcher, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "powershell", "pwsh", "cmd.exe", "netsh", "sc.exe", "certutil", "wmic", "net.exe",
                 })
        {
            Assert.DoesNotContain(forbidden, launcher, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The declared address and the address shown to the operator are the same string.</summary>
    [Fact]
    public void TheAddressShownIsTheAddressOpened()
    {
        var launcher = new NutManager.Infrastructure.AgentConfiguration.WindowsAgentProjectPageLauncher();

        Assert.Equal("https://github.com/marcelodotnet/NutManager", launcher.ProjectPageUrl);
        Assert.StartsWith("https://", launcher.ProjectPageUrl, StringComparison.Ordinal);
    }
}

/// <summary>
/// The startup preference: a boot setting, changed through the service control manager and nowhere
/// else, that never starts or stops anything.
/// </summary>
public sealed class T42ServiceStartupContractTests
{
    [Fact]
    public void OnlyAutomaticAndManualCanBeAskedFor()
    {
        // Disabled is not offered. Turning a boot preference off must not take away the operator's
        // ability to start the agent by hand, and Disabled is exactly that.
        Assert.Equal(
            new[] { AgentServiceStartupPreference.Automatic, AgentServiceStartupPreference.Manual },
            Enum.GetValues<AgentServiceStartupPreference>());
    }

    /// <summary>
    /// The snapshot reports the start type as a typed value rather than only the raw string, so the
    /// interface can say it in either language without parsing an English WMI token.
    /// </summary>
    [Fact]
    public void TheSnapshotCarriesTheStartTypeAndTheAccount()
    {
        var snapshot = new AgentServiceSnapshot(
            AgentServiceState.Running,
            "Auto",
            null,
            AgentServiceStartType.Automatic,
            "LocalSystem");

        Assert.Equal(AgentServiceStartType.Automatic, snapshot.StartType);
        Assert.Equal("LocalSystem", snapshot.Account);
        Assert.True(snapshot.IsInstalled);

        // The defaults keep every existing construction of this record valid and honest: an unread
        // start type is Unknown rather than a guess, and an unread account is absent.
        var minimal = new AgentServiceSnapshot(AgentServiceState.Stopped, null, null);
        Assert.Equal(AgentServiceStartType.Unknown, minimal.StartType);
        Assert.Null(minimal.Account);
    }

    /// <summary>
    /// The start type is changed through the service control manager, and the implementation neither
    /// starts nor stops the service to do it.
    /// </summary>
    [Fact]
    public void ChangingTheStartTypeTouchesNothingElse()
    {
        var administration = T42UnifiedHostTests.Read(
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentServiceAdministration.cs");

        // Every other field of the service configuration is left alone by explicit instruction.
        Assert.Contains("SERVICE_NO_CHANGE", administration, StringComparison.Ordinal);
        Assert.Contains("ChangeServiceConfig", administration, StringComparison.Ordinal);

        var body = administration[administration.IndexOf("SetStartupAsync", StringComparison.Ordinal)..];

        // Not through a command line. No process is started to do this.
        foreach (var forbidden in new[] { "sc.exe", "powershell", "pwsh", "cmd.exe", "wmic", "Process.Start" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }

        // Disabled exists in the type because Windows can report it; it is never written.
        Assert.DoesNotContain("SERVICE_DISABLED", administration, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing was added that makes the window itself start with Windows. The only thing this feature
    /// changes is the service's start type.
    /// </summary>
    [Fact]
    public void NothingAutoLaunchesTheConfigurationWindow()
    {
        foreach (var relativePath in new[]
                 {
                     "src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs",
                     "src/NutManager.Agent.Config/App.axaml.cs",
                     "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentServiceAdministration.cs",
                 })
        {
            var source = T42UnifiedHostTests.Read(relativePath);

            // The autostart mechanisms this feature must not reach for. Deliberately specific:
            // TaskScheduler on its own is the .NET type used for continuations, not Windows.
            foreach (var forbidden in new[]
                     {
                         "CurrentVersion\\Run",
                         "schtasks",
                         "Schedule.Service",
                         "TaskService",
                         "Start Menu\\Programs\\Startup",
                     })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

/// <summary>
/// The build line, the terms document, and the pages this round added or moved.
/// </summary>
public sealed class T42RefinementTests
{
    /// <summary>
    /// The build shows the commit, abbreviated, and leaves the version to the line above it.
    ///
    /// "1.0.1+bd49437e6f63249..." is correct and unreadable at that length, and it repeats a version
    /// already on screen. What is useful is the part after the plus, short enough to take in.
    /// </summary>
    [Theory]
    [InlineData("1.0.1+bd49437e6f632492b4ac0b0ee7ab9d8a1f0e6c11", "bd49437")]
    [InlineData("1.0.1+abcdef123456", "abcdef1")]
    [InlineData("1.0.1+abc", "abc")]
    public void TheBuildIsTheCommitAbbreviated(string informational, string expected) =>
        Assert.Equal(expected, AgentConfigViewModel.ShortenBuild(informational, "1.0.1"));

    /// <summary>
    /// A build with no commit metadata reports what it does have rather than nothing, and never
    /// invents a hash.
    /// </summary>
    [Theory]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("1.0.1+", "1.0.1+")]
    public void ABuildWithoutCommitMetadataFallsBack(string informational, string expected) =>
        Assert.Equal(expected, AgentConfigViewModel.ShortenBuild(informational, "1.0.1"));

    [Fact]
    public void AnAbsentInformationalVersionFallsBackToTheVersion() =>
        Assert.Equal("1.0.1", AgentConfigViewModel.ShortenBuild(null, "1.0.1"));

    /// <summary>
    /// The terms are the canonical documents, parsed - not a paraphrase kept beside them.
    ///
    /// The repository maintenance notes in those files say which copy is canonical and what still has
    /// to be regenerated. They are not part of the legal text and must never reach an operator, which
    /// is the same rule the installer RTF generator applies to the same two files.
    /// </summary>
    [Fact]
    public void TheTermsParserKeepsTheLegalTextAndDropsTheMaintenanceNotes()
    {
        var markdown = string.Join(
            "\n",
            "# Terms of Use",
            "",
            "<!--",
            "  TRANSLATION. Regenerate the RTF after editing.",
            "  PENDING: re-synchronise before tagging.",
            "-->",
            "",
            "**Last updated:** 27 August 2026",
            "",
            "## 1. About",
            "",
            "NutManager is **open-source** software.",
            "",
            "- A first item",
            "- A second item",
            "",
            "---");

        var blocks = AgentTermsDocument.Parse(markdown);

        Assert.Equal(AgentTermsBlockKind.Title, blocks[0].Kind);
        Assert.Equal("Terms of Use", blocks[0].Text);

        // Nothing from inside the comment survives, on any line of it.
        Assert.DoesNotContain(blocks, block => block.Text.Contains("TRANSLATION", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, block => block.Text.Contains("Regenerate", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, block => block.Text.Contains("PENDING", StringComparison.Ordinal));

        Assert.Contains(blocks, block =>
            block.Kind == AgentTermsBlockKind.Heading && block.Text == "1. About");

        // Emphasis markers are presentation and go; the words between them are the term and stay.
        Assert.Contains(blocks, block =>
            block.Kind == AgentTermsBlockKind.Paragraph &&
            block.Text == "NutManager is open-source software.");
        Assert.DoesNotContain(blocks, block => block.Text.Contains("**", StringComparison.Ordinal));

        Assert.Equal(2, blocks.Count(block => block.Kind == AgentTermsBlockKind.Bullet));

        // A rule is a separator, and the headings already separate the sections.
        Assert.DoesNotContain(blocks, block => block.Text == "---");
    }

    /// <summary>Both canonical documents are shipped, so neither language opens an empty page.</summary>
    [Fact]
    public void BothCanonicalTermsDocumentsAreLinkedIntoTheWindow()
    {
        var project = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/NutManager.Agent.Config.csproj");

        Assert.Contains("TERMS-OF-USE.md", project, StringComparison.Ordinal);
        Assert.Contains("TERMS-OF-USE.en-US.md", project, StringComparison.Ordinal);

        // Linked from docs rather than copied: one document, two renderings - this window and the RTF
        // the installer shows. A copy here would drift the first time either was edited.
        Assert.Contains("AvaloniaResource", project, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(T42UnifiedHostTests.RepositoryRoot(), "docs/TERMS-OF-USE.md")));
        Assert.True(File.Exists(Path.Combine(T42UnifiedHostTests.RepositoryRoot(), "docs/TERMS-OF-USE.en-US.md")));
    }

    /// <summary>
    /// Terms is a surface of this window, reached from About and returning to it. Not a browser, not
    /// a second window, and not another process.
    /// </summary>
    [Fact]
    public void TheTermsPageIsInternalAndScrollsOnItsOwn()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var viewModel = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs");

        Assert.Contains("IsVisible=\"{Binding ShowTerms}\"", window, StringComparison.Ordinal);
        Assert.Contains("Surface == AgentConfigSurface.Terms", viewModel, StringComparison.Ordinal);

        // There and back, and back lands on Settings so About is still the open tab.
        Assert.Contains("private void OpenTerms()", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "private void CloseTerms() => Surface = AgentConfigSurface.Settings;",
            viewModel,
            StringComparison.Ordinal);

        var page = window[window.IndexOf("IsVisible=\"{Binding ShowTerms}\"", StringComparison.Ordinal)..];
        page = page[..page.IndexOf("settings -->", StringComparison.Ordinal)];

        Assert.Contains("x:Name=\"TermsBack\"", page, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseTermsCommand}\"", page, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding Strings[Terms.Back]}\"", page, StringComparison.Ordinal);

        // The page scrolls; the window does not grow and does not resize.
        Assert.Contains("<ScrollViewer", page, StringComparison.Ordinal);
        Assert.Contains("Width=\"800\"", window, StringComparison.Ordinal);
        Assert.Contains("Height=\"600\"", window, StringComparison.Ordinal);
        Assert.Contains("CanResize=\"False\"", window, StringComparison.Ordinal);

        // Nothing on this page opens anything outside the process.
        Assert.DoesNotContain("OpenProjectPageCommand", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// Apply and Cancel belong to the configuration draft. Settings applies each change as it is made
    /// and the terms are a document, so a Save button beside either would promise something is
    /// waiting to be saved.
    /// </summary>
    [Fact]
    public void ApplyAndCancelAreHiddenOutsideTheDraft()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var viewModel = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs");

        // Diagnostics is absent from the condition on purpose: it is a read-only view of the very
        // configuration the draft belongs to, and it keeps the buttons.
        Assert.Contains(
            "public bool ShowActionBar => !ShowSettings && !ShowTerms;",
            viewModel,
            StringComparison.Ordinal);

        var apply = window.IndexOf("x:Name=\"ApplyReasonHost\"", StringComparison.Ordinal);
        var group = window.LastIndexOf("<StackPanel Grid.Column=\"2\"", apply, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowActionBar}\"", window[group..apply], StringComparison.Ordinal);

        // The service actions are not part of the draft and stay on every surface.
        var restart = window.IndexOf("x:Name=\"RestartServiceAction\"", StringComparison.Ordinal);
        var restartEnd = window.IndexOf("</Button>", restart, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsVisible=\"{Binding ShowActionBar}\"",
            window[restart..restartEnd],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The header button announces what pressing it does, in both languages, and the name it gives is
    /// the one the tooltip shows - a button whose accessible name differed from its tooltip would be
    /// two controls to anyone reading it through a screen reader.
    /// </summary>
    [Fact]
    public void TheHeaderButtonIsNamedForItsActionInBothLanguages()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var strings = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/Localization/AgentConfigStrings.cs");
        var viewModel = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs");

        var button = window.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal);
        var closing = window.IndexOf("</Button>", button, StringComparison.Ordinal);
        var markup = window[button..closing];

        // One string for both, and it follows the glyph rather than naming a fixed destination.
        Assert.Contains("ToolTip.Tip=\"{Binding HeaderActionText}\"", markup, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding HeaderActionText}\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "HeaderActionText => ShowHomeAction ? Strings[\"Header.Home\"] : Strings[\"Settings.Title\"];",
            viewModel,
            StringComparison.Ordinal);

        Assert.Contains("[\"Settings.Title\"] = \"Configurações\"", strings, StringComparison.Ordinal);
        Assert.Contains("[\"Settings.Title\"] = \"Settings\"", strings, StringComparison.Ordinal);
        Assert.Contains("[\"Header.Home\"] = \"Início\"", strings, StringComparison.Ordinal);
        Assert.Contains("[\"Header.Home\"] = \"Home\"", strings, StringComparison.Ordinal);
    }

    /// <summary>
    /// The house does not turn like a cog.
    ///
    /// The rotation comes from a class, so the class must not be on the house. Two elements rather
    /// than one with a swapped geometry is what guarantees that.
    /// </summary>
    [Fact]
    public void TheHomeGlyphDoesNotTakeTheGearRotation()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var home = window.IndexOf("Data=\"{DynamicResource AgentIconHome}\"", StringComparison.Ordinal);
        var element = window.LastIndexOf("<PathIcon", home, StringComparison.Ordinal);
        Assert.DoesNotContain("nut-icon-gear", window[element..home], StringComparison.Ordinal);

        Assert.Contains(
            "<Style Selector=\"Button.agent-settings-button:pointerover PathIcon.agent-home-glyph\">",
            window,
            StringComparison.Ordinal);

        // It lifts rather than spins, and still never loops.
        var motion = window[window.IndexOf(
            "Button.agent-settings-button:pointerover PathIcon.agent-home-glyph", StringComparison.Ordinal)..];
        motion = motion[..motion.IndexOf("</Style>", StringComparison.Ordinal)];
        Assert.Contains("scale(1.08)", motion, StringComparison.Ordinal);
        Assert.DoesNotContain("rotate", motion, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reset section heading states what it is; the question mark belongs to the confirmation,
    /// which is the thing that actually asks.
    /// </summary>
    [Fact]
    public void TheResetSectionHeadingIsNotAQuestion()
    {
        var strings = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/Localization/AgentConfigStrings.cs");

        Assert.Contains(
            "[\"Settings.Https.Reset.Title\"] = \"Resetar configuração HTTPS\"",
            strings,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"Settings.Https.Reset.Title\"] = \"Reset HTTPS configuration\"",
            strings,
            StringComparison.Ordinal);

        // The confirmation still asks, and still ends in a question mark.
        Assert.Contains(
            "[\"Https.Reset.Title\"] = \"Resetar configuração HTTPS?\"",
            strings,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointCopyFeedbackIsAnAnchoredNonLayoutPopup()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var popupStart = window.IndexOf("<Popup x:Name=\"EndpointCopyPopup\"", StringComparison.Ordinal);
        var popupEnd = window.IndexOf("</Popup>", popupStart, StringComparison.Ordinal);
        var popup = window[popupStart..popupEnd];

        Assert.True(popupStart >= 0);
        Assert.Contains("PlacementTarget=\"{Binding #CopyEndpointButton}\"", popup, StringComparison.Ordinal);
        Assert.Contains("Placement=\"BottomEdgeAlignedRight\"", popup, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding IsToastPopupOpen}\"", popup, StringComparison.Ordinal);
        Assert.Contains("Classes.visible=\"{Binding IsToastVisible}\"", popup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", popup, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", popup, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"False\"", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Row=", popup, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(window, "x:Name=\"EndpointCopyPopup\"").Cast<Match>());
    }

    [Fact]
    public void EndpointCopyFeedbackUsesTheExistingIconAndCubicMotion()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        Assert.Contains("Converter={x:Static converters:AgentConfigConverters.ToastIcon}", window, StringComparison.Ordinal);
        Assert.Contains("DoubleTransition Property=\"Opacity\" Duration=\"0:0:0.18\" Easing=\"CubicEaseOut\"", window, StringComparison.Ordinal);
        Assert.Contains("TransformOperationsTransition Property=\"RenderTransform\" Duration=\"0:0:0.18\" Easing=\"CubicEaseOut\"", window, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointCopyFeedbackHasTheRequiredTextInBothLanguages()
    {
        var strings = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/Localization/AgentConfigStrings.cs");

        Assert.Contains("[\"Toast.EndpointCopied\"] = \"Copiado!\"", strings, StringComparison.Ordinal);
        Assert.Contains("[\"Toast.EndpointCopyFailed\"] = \"Não foi possível copiar.\"", strings, StringComparison.Ordinal);
        Assert.Contains("[\"Toast.EndpointCopied\"] = \"Copied!\"", strings, StringComparison.Ordinal);
        Assert.Contains("[\"Toast.EndpointCopyFailed\"] = \"Could not copy.\"", strings, StringComparison.Ordinal);
    }
}

/// <summary>
/// The listener observation, and the boundary it lives behind.
///
/// The row that reports whether the agent is reachable is the one piece of this window that changes
/// while nobody is touching it, and it is now observed rather than composed. What is asserted here is
/// the adapter that does the observing - against an ephemeral loopback socket this test owns, never a
/// real agent, a real service or a real certificate - and the rules that keep the observation cheap
/// and read-only.
/// </summary>
public sealed class T42ListenerProbeTests
{
    /// <summary>
    /// An endpoint that is there answers, and the answer names no failure.
    ///
    /// The socket is bound to IPv4 while the name is asked for by "localhost", which resolves to the
    /// IPv6 loopback first on Windows. That is the case that fails when the addresses are attempted in
    /// order: a connection to the IPv6 loopback with nothing behind it is not refused, it hangs, so
    /// the first address consumes the whole budget and a listener that is up reports as unreachable.
    /// It is the shape of the real thing - an agent host normally resolves to both families.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AnOpenPortIsReportedListening()
    {
        using var endpoint = new LoopbackEndpoint();
        var probe = new WindowsAgentHttpsListenerProbe();

        var observation = await probe.ProbeAsync(endpoint.Binding, CancellationToken.None);

        Assert.Equal(AgentListenerReachability.Listening, observation.State);
        Assert.Null(observation.Detail);
    }

    /// <summary>
    /// A port nobody is on is reported unreachable, and says which socket error said so.
    ///
    /// The port is one this test held and released, so it is free on this machine at this moment
    /// without guessing at a number somebody else might be using.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AClosedPortIsReportedUnreachableWithItsSocketError()
    {
        int port;
        using (var endpoint = new LoopbackEndpoint()) port = endpoint.Binding.Port;

        var probe = new WindowsAgentHttpsListenerProbe();
        var binding = new AgentHttpsBinding("localhost", port, Thumbprint);

        var observation = await probe.ProbeAsync(binding, CancellationToken.None);

        Assert.Equal(AgentListenerReachability.Unreachable, observation.State);
        Assert.False(string.IsNullOrWhiteSpace(observation.Detail));
        Assert.DoesNotContain("   at ", observation.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancelling the observation is not a verdict about the endpoint.
    ///
    /// It has to propagate rather than be recorded as unreachable: the window closing while a probe is
    /// in flight would otherwise write a red row on its way out.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task CancellationIsNotAnAnswer()
    {
        using var endpoint = new LoopbackEndpoint();
        var probe = new WindowsAgentHttpsListenerProbe();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ProbeAsync(endpoint.Binding, cancelled.Token));
    }

    /// <summary>
    /// The timeout is short enough to live inside the polling period, and long enough to be true.
    ///
    /// A probe that could wait as long as the named-pipe client does would spend most of a one-second
    /// cadence waiting, and one that gave up in a few milliseconds would report a live endpoint as
    /// dead on any machine under load.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheProbeTimeoutFitsInsideThePollingPeriod()
    {
        Assert.True(WindowsAgentHttpsListenerProbe.DefaultTimeout < TimeSpan.FromSeconds(2));
        Assert.True(WindowsAgentHttpsListenerProbe.DefaultTimeout >= TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// The observation opens a socket and does nothing else.
    ///
    /// No process, no shell, no netsh, no sc.exe, no netstat, no WMI - and no request, no credential
    /// and no write. This runs once a second on an elevated administrative window, so the list of
    /// things it is allowed to do is asserted rather than remembered.
    /// </summary>
    [Fact]
    public void TheProbeRunsNoProcessAndWritesNothing()
    {
        var probe = T42UnifiedHostTests.Read(
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentHttpsListenerProbe.cs");

        foreach (var forbidden in new[]
                 {
                     "Process.Start", "ProcessStartInfo", "ShellExecute", "powershell", "pwsh",
                     "cmd.exe", "netsh", "sc.exe", "netstat", "wmic", "ManagementObjectSearcher",
                     "Registry", "File.", "Directory.",
                 })
        {
            Assert.DoesNotContain(forbidden, probe, StringComparison.OrdinalIgnoreCase);
        }

        // It connects, and that is all it does with the connection.
        Assert.Contains("new TcpClient(address.AddressFamily)", probe, StringComparison.Ordinal);
        Assert.Contains("ConnectAsync", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAsync", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("GetStream", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticateAsClient", probe, StringComparison.Ordinal);
    }

    /// <summary>
    /// The monitor belongs to the window, and the service host has never heard of it.
    ///
    /// The whole point of the unified host is that one image runs two things that share no state. A
    /// timer the configuration window needs must not appear on the branch that runs as a service,
    /// where there is no window to update and nobody to watch it.
    /// </summary>
    [Fact]
    public void OnlyTheConfigurationWindowRunsTheMonitor()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml.cs");

        Assert.Contains("Opened += ", window, StringComparison.Ordinal);
        Assert.Contains("StartListenerMonitor()", window, StringComparison.Ordinal);
        Assert.Contains("StopListenerMonitor()", window, StringComparison.Ordinal);

        foreach (var path in new[]
                 {
                     "src/NutManager.Agent/Program.cs",
                     "src/NutManager.Agent/NutAgentWindowsService.cs",
                     "src/NutManager.Agent.Config/AgentConfigHost.cs",
                 })
        {
            var source = T42UnifiedHostTests.Read(path);
            Assert.DoesNotContain("ListenerMonitor", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IAgentHttpsListenerProbe", source, StringComparison.Ordinal);
        }
    }

    /// <summary>The real adapter is the one the window is given.</summary>
    [Fact]
    public void TheWindowIsComposedWithTheRealProbe()
    {
        var app = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/App.axaml.cs");

        Assert.Contains("listenerProbe: new WindowsAgentHttpsListenerProbe()", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// The monitor observes and never acts.
    ///
    /// Everything that changes the machine stays where an operator put it behind a button. A watcher
    /// that started a service because it found the listener down would be taking an administrative
    /// action nobody asked for, once a second, from a background loop.
    /// </summary>
    [Fact]
    public void TheMonitorNeverActsOnWhatItObserves()
    {
        var viewModel = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs");

        var start = viewModel.IndexOf("---------------- listener monitor", StringComparison.Ordinal);
        var end = viewModel.IndexOf("---------------- status and diagnostics", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start, "The listener monitor section was not found.");
        var monitor = viewModel[start..end];

        foreach (var forbidden in new[]
                 {
                     "_service.StartAsync", "_service.RestartAsync", "_service.StopAsync",
                     "_resources.Apply", "_resources.Remove", "_store.Write",
                     "_certificates.", "SetStartupAsync",
                 })
        {
            Assert.DoesNotContain(forbidden, monitor, StringComparison.Ordinal);
        }

        // One loop, cancelled with the window, and one probe at a time.
        Assert.Contains("if (_listenerMonitor is not null) return;", monitor, StringComparison.Ordinal);
        Assert.Contains("monitor.Cancel();", monitor, StringComparison.Ordinal);
        Assert.Contains("_listenerProbeGate.WaitAsync(0,", monitor, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(1)", monitor, StringComparison.Ordinal);
    }

    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    /// <summary>
    /// An ephemeral socket on the loopback interface, owned and closed by the test.
    ///
    /// Port zero, so the operating system picks one that is free rather than the test claiming a
    /// number that might belong to something on the machine running it. Nothing is ever sent over it;
    /// it exists to be connectable, which is the entire question the probe asks.
    /// </summary>
    private sealed class LoopbackEndpoint : IDisposable
    {
        private readonly TcpListener _listener;

        public LoopbackEndpoint()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Binding = new AgentHttpsBinding("localhost", port, Thumbprint);
        }

        public AgentHttpsBinding Binding { get; }

        public void Dispose() => _listener.Stop();
    }
}

/// <summary>
/// The settings panels an operator acts on, and the one boundary that registers a service.
///
/// Layout is asserted structurally rather than by pixel: what matters is the order things appear in,
/// which control carries which action, and that the parts that must not exist do not. The native
/// registration is asserted the same way plus by its own rules, because the one thing that cannot be
/// exercised on a build machine is CreateService itself.
/// </summary>
public sealed class T42ServiceAdministrationSettingsTests
{
    /// <summary>
    /// The switch reads under its heading, not opposite it.
    ///
    /// It used to sit in the far column of the heading row, which put the control as far from the
    /// words explaining it as the card allowed. Asserted by position rather than by margin, so the
    /// test survives spacing being tuned.
    /// </summary>
    [Fact]
    public void TheStartupSwitchSitsBelowItsHeading()
    {
        var panel = GeneralPanel();

        var heading = panel.IndexOf("x:Name=\"StartupHeading\"", StringComparison.Ordinal);
        var toggle = panel.IndexOf("x:Name=\"StartWithWindowsSwitch\"", StringComparison.Ordinal);
        var description = panel.IndexOf("Settings.Startup.Description", StringComparison.Ordinal);
        var result = panel.IndexOf("x:Name=\"StartupResult\"", StringComparison.Ordinal);

        Assert.True(heading >= 0 && toggle > heading, "The switch belongs under the heading.");
        Assert.True(description > toggle, "The description belongs under the switch.");
        Assert.True(result > description, "The result of an action belongs last.");

        // The heading no longer shares a row with anything, so there is no far column to sit in.
        var headingToToggle = panel[heading..toggle];
        Assert.DoesNotContain("ColumnDefinitions", headingToToggle, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Column=", headingToToggle, StringComparison.Ordinal);

        // Left, with the help button beside it rather than across the card.
        Assert.Contains("HorizontalAlignment=\"Left\"", headingToToggle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The way to install is offered only while it is needed, and it navigates rather than acts.
    /// </summary>
    [Fact]
    public void TheHelpButtonIsBesideTheSwitchAndOnlyWhenThereIsNoService()
    {
        var panel = GeneralPanel();

        var toggle = panel.IndexOf("x:Name=\"StartWithWindowsSwitch\"", StringComparison.Ordinal);
        var help = panel.IndexOf("x:Name=\"StartupInstallHelp\"", StringComparison.Ordinal);
        var description = panel.IndexOf("Settings.Startup.Description", StringComparison.Ordinal);

        Assert.True(help > toggle && help < description, "The help button sits beside the switch.");
        Assert.Contains("IsVisible=\"{Binding ShowStartupHelp}\"", panel, StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding ShowServiceInstallationCommand}\"", panel, StringComparison.Ordinal);

        // A catalog glyph, not a typed question mark.
        Assert.Contains("{DynamicResource AgentIconHelp}", panel, StringComparison.Ordinal);

        // It stands beside the switch as an equal: a circle of the same height, with no border of its
        // own - the glyph already draws a ring, and a second one around it would be two circles.
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var style = window.IndexOf("Selector=\"Button.agent-help-button\"", StringComparison.Ordinal);
        var end = window.IndexOf("</Style>", style, StringComparison.Ordinal);
        Assert.True(style > 0 && end > style);

        var rule = window[style..end];
        var width = Regex.Match(rule, @"Property=""Width"" Value=""(\d+)""").Groups[1].Value;
        var height = Regex.Match(rule, @"Property=""Height"" Value=""(\d+)""").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(width));
        Assert.Equal(width, height);
        Assert.Contains("Property=\"BorderThickness\" Value=\"0\"", rule, StringComparison.Ordinal);
        Assert.Contains("Property=\"CornerRadius\" Value=\"999\"", rule, StringComparison.Ordinal);

        // Named for what it does, in both languages, and reachable by a screen reader.
        Assert.Contains(
            "ToolTip.Tip=\"{Binding Strings[Settings.Startup.Install]}\"", panel, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding Strings[Settings.Startup.Install]}\"",
            panel,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The result line carries a disc and is emphasised, and the kind decides which disc.
    ///
    /// Manual is the exclamation rather than the question mark it used to be: the operator is being
    /// told a consequence, not asked something.
    /// </summary>
    [Fact]
    public void TheStartupResultIsAnEmphasisedLineWithADisc()
    {
        var panel = GeneralPanel();
        var start = panel.IndexOf("x:Name=\"StartupResult\"", StringComparison.Ordinal);
        Assert.True(start > 0);

        var result = panel[start..];
        Assert.Contains("IsVisible=\"{Binding HasStartupResult}\"", result, StringComparison.Ordinal);
        Assert.Contains("FontWeight=\"SemiBold\"", result, StringComparison.Ordinal);
        Assert.Contains(
            "Converter={x:Static converters:AgentConfigConverters.SettingsFeedbackIcon}",
            result,
            StringComparison.Ordinal);
        Assert.Contains(
            "Converter={x:Static converters:AgentConfigConverters.SettingsFeedbackBrush}",
            result,
            StringComparison.Ordinal);

        // Success is a circled check, the consequence is a circled exclamation, and neither is an
        // emoji or a literal character.
        var icons = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Presentation/AgentConfigIcons.cs");
        Assert.Contains(
            "(\"AgentIconStateReady\", MaterialIconKind.CheckCircle)", icons, StringComparison.Ordinal);
        Assert.Contains(
            "(\"AgentIconFeedbackWarning\", MaterialIconKind.AlertCircle)", icons, StringComparison.Ordinal);
        Assert.Contains(
            "(\"AgentIconHelp\", MaterialIconKind.HelpCircleOutline)", icons, StringComparison.Ordinal);

        var converters = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/Presentation/AgentConfigConverters.cs");
        Assert.Contains(
            "AgentSettingsFeedback.Success => \"AgentIconStateReady\"", converters, StringComparison.Ordinal);
        Assert.Contains(
            "AgentSettingsFeedback.Warning => \"AgentIconFeedbackWarning\"", converters, StringComparison.Ordinal);
        Assert.Contains(
            "AgentSettingsFeedback.Warning => \"NutWarningBrush\"", converters, StringComparison.Ordinal);
        Assert.Contains(
            "AgentSettingsFeedback.Success => \"NutHealthyBrush\"", converters, StringComparison.Ordinal);
    }

    /// <summary>The reset names the transport it resets, and stands clear of the sentence above it.</summary>
    [Fact]
    public void TheResetActionNamesTheTransportAndHasRoomOfItsOwn()
    {
        var strings = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/Localization/AgentConfigStrings.cs");

        Assert.Contains("[\"Https.Reset\"] = \"Resetar HTTPS\"", strings, StringComparison.Ordinal);
        Assert.Contains("[\"Https.Reset\"] = \"Reset HTTPS\"", strings, StringComparison.Ordinal);

        // The heading above it is untouched: it is a section, not the question the dialog asks.
        Assert.Contains(
            "[\"Settings.Https.Reset.Title\"] = \"Resetar configura\u00e7\u00e3o HTTPS\"",
            strings,
            StringComparison.Ordinal);

        var panel = GeneralPanel();
        var description = panel.IndexOf("Settings.Https.Reset.Description", StringComparison.Ordinal);
        var button = panel.IndexOf("x:Name=\"SettingsResetHttps\"", StringComparison.Ordinal);
        Assert.True(description > 0 && button > description);

        // Separated by a margin of its own rather than left at the paragraph spacing.
        Assert.Contains("Margin=\"0,10,0,0\"", panel[description..button], StringComparison.Ordinal);
    }

    /// <summary>
    /// The Agent panel installs first and reports second, on one surface with a rule between.
    /// </summary>
    [Fact]
    public void TheAgentPanelInstallsFirstAndReportsSecond()
    {
        var panel = AgentPanel();

        var installHeading = panel.IndexOf("Settings.Agent.Install.Title", StringComparison.Ordinal);
        var button = panel.IndexOf("x:Name=\"AgentInstallService\"", StringComparison.Ordinal);
        var divider = panel.IndexOf("Classes=\"nut-divider\"", StringComparison.Ordinal);
        var section = panel.IndexOf("Settings.Agent.Section", StringComparison.Ordinal);
        var state = panel.IndexOf("ServiceStateText", StringComparison.Ordinal);

        Assert.True(installHeading >= 0, "The installation section is missing.");
        Assert.True(button > installHeading, "The button belongs under its own heading.");
        Assert.True(divider > button, "The rule separates the two sections.");
        Assert.True(section > divider, "The reported values come after the rule.");
        Assert.True(state > section, "The values belong under their heading.");

        // Left, driven by the machine, and never hidden once it has been used: the button element
        // itself carries no visibility binding, only the enabled state its command decides.
        var element = panel[button..panel.IndexOf("</Button>", button, StringComparison.Ordinal)];
        Assert.Contains("HorizontalAlignment=\"Left\"", element, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible=", element, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding InstallServiceCommand}\"", element, StringComparison.Ordinal);

        // One surface. No card of its own for the new section, and nothing to scroll.
        Assert.DoesNotContain("agent-shell-card", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("nut-card\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("<ScrollViewer", panel, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(panel, "nut-divider"));
    }

    /// <summary>
    /// Two headings, both of them named, and the sentence that used to say the tab does nothing is
    /// gone - because it now does something.
    /// </summary>
    [Fact]
    public void TheAgentPanelHasExactlyTheTwoAgreedHeadings()
    {
        var strings = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/Localization/AgentConfigStrings.cs");

        foreach (var heading in new[]
                 {
                     "[\"Settings.Agent.Install.Title\"] = \"Instala\u00e7\u00e3o do servi\u00e7o\"",
                     "[\"Settings.Agent.Section\"] = \"Servi\u00e7o e comunica\u00e7\u00e3o\"",
                     "[\"Settings.Agent.Install.Title\"] = \"Service installation\"",
                     "[\"Settings.Agent.Section\"] = \"Service and communication\"",
                 })
        {
            Assert.Contains(heading, strings, StringComparison.Ordinal);
        }

        // Not split into a "Service" heading and a "Communication" heading.
        Assert.DoesNotContain("[\"Settings.Agent.Communication\"]", strings, StringComparison.Ordinal);

        // The read-only sentence is gone from the strings and from the window, and was not replaced
        // by another one saying the same thing.
        Assert.DoesNotContain("Settings.Agent.Description", strings, StringComparison.Ordinal);
        Assert.DoesNotContain("Somente leitura", strings, StringComparison.Ordinal);
        Assert.DoesNotContain("Read-only", strings, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Agent.Description", AgentPanel(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The mark in the header is the image, with nothing drawn around it.
    ///
    /// The frame was ours: a rounded panel with its own surface and border, wrapped around an image
    /// that already has edges of its own. The size and the spacing are what they were.
    /// </summary>
    [Fact]
    public void TheHeaderLogoHasNoFrameAroundIt()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var header = window.IndexOf("x:Name=\"AgentMainHeader\"", StringComparison.Ordinal);
        var title = window.IndexOf("nut-product-title", header, StringComparison.Ordinal);
        Assert.True(header > 0 && title > header);

        var logo = window[header..title];
        Assert.Contains("x:Name=\"AgentHeaderLogo\"", logo, StringComparison.Ordinal);
        Assert.Contains(
            "avares://NutManager.Agent.Config/Assets/Branding/NutManager.png", logo, StringComparison.Ordinal);

        // Nothing between the header grid and the image draws a container.
        Assert.DoesNotContain("<Border", logo, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius", logo, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderThickness", logo, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush", logo, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=", logo, StringComparison.Ordinal);

        // Same size and same gap as before, so the header does not shift.
        Assert.Contains("Width=\"78\"", logo, StringComparison.Ordinal);
        Assert.Contains("Height=\"78\"", logo, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,0,13,0\"", logo, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registration is scoped to one service by construction, and cannot be pointed at another.
    ///
    /// The contract takes nothing, so there is no parameter through which a name, a path, a command
    /// line or an account could arrive from the window. Everything CreateService needs is a constant
    /// in the adapter or comes from the running process.
    /// </summary>
    [Fact]
    public void RegistrationIsScopedToOneServiceAndCannotBeRedirected()
    {
        var contract = T42UnifiedHostTests.Read("src/NutManager.Core/Agent/AgentAdministrationContracts.cs");
        Assert.Contains(
            "Task<AgentServiceInstallation> InstallAsync(CancellationToken cancellationToken);",
            contract,
            StringComparison.Ordinal);

        var installer = T42UnifiedHostTests.Read(
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentServiceInstallation.cs");

        // Fixed by the implementation, every one of them.
        Assert.Contains(
            "ServiceName = WindowsAgentServiceAdministration.ServiceName", installer, StringComparison.Ordinal);
        Assert.Contains("DisplayName = \"NutManager Agent\"", installer, StringComparison.Ordinal);
        Assert.Contains("ServiceArgument = \"--service\"", installer, StringComparison.Ordinal);
        Assert.Contains("HostFileName = \"NutManager.Agent.exe\"", installer, StringComparison.Ordinal);
        Assert.Contains("ServiceAutoStart = 0x00000002", installer, StringComparison.Ordinal);
        Assert.Contains("ServiceWin32OwnProcess = 0x00000010", installer, StringComparison.Ordinal);

        // LocalSystem, expressed as the null account CreateService documents - so no password exists
        // to pass, store or leak.
        Assert.Contains("lpServiceStartName: null", installer, StringComparison.Ordinal);
        Assert.Contains("lpPassword: null", installer, StringComparison.Ordinal);

        // No public way in, and no shell.
        Assert.Contains("internal sealed class WindowsAgentServiceInstallation", installer, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "Process.Start", "ProcessStartInfo", "ShellExecute", "powershell", "pwsh",
                     "cmd.exe", "netsh", "sc.exe", "wmic", "ManagementObjectSearcher", "schtasks",
                 })
        {
            Assert.DoesNotContain(forbidden, installer, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Registering never starts what it registered.
    ///
    /// The installer calls CreateService and stops. StartService appears nowhere in it, and the view
    /// model's registration path does not reach for the start command either.
    /// </summary>
    [Fact]
    public void RegistrationNeverStartsTheService()
    {
        var installer = T42UnifiedHostTests.Read(
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentServiceInstallation.cs");
        Assert.DoesNotContain("StartService", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceController", installer, StringComparison.Ordinal);

        var viewModel = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/ViewModels/AgentConfigViewModel.cs");
        var start = viewModel.IndexOf("private async Task InstallServiceAsync", StringComparison.Ordinal);
        var end = viewModel.IndexOf("private string? _installFailure", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);

        var command = viewModel[start..end];
        Assert.Contains("_service.InstallAsync", command, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "_service.StartAsync", "_service.RestartAsync", "_service.StopAsync",
                     "_store.Write", "_resources.",
                 })
        {
            Assert.DoesNotContain(forbidden, command, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// It registers the process that is running, quoted, with the service switch - and refuses
    /// anything else.
    ///
    /// The quoting is the part that matters most: an unquoted image path under Program Files lets
    /// Windows try the wrong executable first, which is a well-known way to hijack a service and not
    /// one this product is going to introduce from a button.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheRegisteredImagePathIsTheRunningHostQuotedWithTheServiceSwitch()
    {
        const string host = @"C:\Program Files\NutManager Agent\NutManager.Agent.exe";

        var installation = new WindowsAgentServiceInstallation(() => host, path => path == host);

        Assert.Equal($"\"{host}\" --service", installation.ResolveImagePath());
    }

    /// <summary>A host that is not the agent, or is not there, registers nothing at all.</summary>
    [Theory]
    [SupportedOSPlatform("windows")]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("NutManager.Agent.exe", true)]
    [InlineData(@"C:\Tools\evil.exe", true)]
    [InlineData(@"C:\Tools\NutManager.App.exe", true)]
    [InlineData(@"C:\Tools\NutManager.Agent.exe", false)]
    public void RegistrationRefusesAnyHostThatIsNotTheAgentApphost(string? host, bool exists)
    {
        var installation = new WindowsAgentServiceInstallation(() => host, _ => exists);

        Assert.Null(installation.ResolveImagePath());
    }

    /// <summary>
    /// The Event Log source stays the installer's.
    ///
    /// The agent refuses to run without its audit trail rather than creating one, which is what makes
    /// the trail trustworthy - so a button that quietly created the source would remove the very
    /// property that boundary exists for. Registering the service is what an application may own.
    /// </summary>
    [Fact]
    public void RegistrationDoesNotCreateTheEventLogSource()
    {
        var installer = T42UnifiedHostTests.Read(
            "src/NutManager.Infrastructure/AgentConfiguration/WindowsAgentServiceInstallation.cs");

        Assert.DoesNotContain("EventLog", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry", installer, StringComparison.Ordinal);

        // And the installer package still owns it.
        var package = T42UnifiedHostTests.Read("installer/Agent/Package.wxs");
        Assert.Contains("AgentEventLogSource", package, StringComparison.Ordinal);
    }

    private static string GeneralPanel() => Panel("IsGeneralTab", "IsAppearanceTab");

    private static string AgentPanel() => Panel("IsAgentTab", "IsAboutTab");

    private static string Panel(string from, string to)
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var start = window.IndexOf($"IsVisible=\"{{Binding {from}}}\"", StringComparison.Ordinal);
        var end = window.IndexOf($"IsVisible=\"{{Binding {to}}}\"", start, StringComparison.Ordinal);

        Assert.True(start > 0 && end > start, $"The {from} panel was not found.");
        return window[start..end];
    }
}
