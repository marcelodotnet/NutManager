using System.Text.RegularExpressions;
using NutManager.Agent;
using NutManager.Agent.Config.Localization;
using NutManager.Agent.Config.ViewModels;
using NutManager.Core.Agent;
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
    [Fact]
    public void TheGearOpensSettingsFromTheHeader()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var header = window.IndexOf("x:Name=\"AgentMainHeader\"", StringComparison.Ordinal);
        var surface = window.IndexOf("x:Name=\"ConfigurationSurface\"", header, StringComparison.Ordinal);
        var markup = window[header..surface];

        var gear = markup.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal);
        var diagnostics = markup.IndexOf("Command=\"{Binding ToggleDiagnosticsCommand}\"", StringComparison.Ordinal);

        Assert.True(gear >= 0, "The header must carry the settings button.");
        Assert.True(diagnostics > gear, "The gear sits to the left of Diagnostics.");

        Assert.Contains("Command=\"{Binding ToggleSettingsCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding Strings[Settings.Title]}\"", markup, StringComparison.Ordinal);

        // The same round shape as the theme button it replaced, so the header keeps one vocabulary.
        Assert.Contains("Classes=\"agent-settings-button\"", markup, StringComparison.Ordinal);
        Assert.Contains("Button.agent-settings-button", window, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsSurfaceHasFourTabs()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        Assert.Contains("IsVisible=\"{Binding ShowSettings}\"", window, StringComparison.Ordinal);

        // Each header is an icon beside its localized name, so the strip reads as navigation rather
        // than as four headings.
        foreach (var tab in new[] { "General", "Appearance", "Agent", "About" })
        {
            Assert.Contains($"Text=\"{{Binding Strings[Settings.Tab.{tab}]}}\"", window, StringComparison.Ordinal);
        }

        Assert.Equal(4, Regex.Matches(window, "<TabItem>").Count);
        Assert.Equal(4, Regex.Matches(window, "<TabItem.Header>").Count);
        Assert.Equal(4, Regex.Matches(window, "Classes=\"agent-tab-icon\"").Count);
        Assert.Single(Regex.Matches(window, "<TabControl "));

        // Under the card title, not over it: these move you around the page, they are not the page.
        Assert.Contains("<Style Selector=\"TabItem\">", window, StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"FontSize\" Value=\"17\" />",
            window,
            StringComparison.Ordinal);

        // Selected takes the accent; the rest stay secondary.
        Assert.Contains("TabItem:selected", window, StringComparison.Ordinal);
        Assert.Contains("TabItem:pointerover", window, StringComparison.Ordinal);
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
    /// The Agent tab reports and does not act. Every binding on it is a read, and no command, button
    /// or editable field appears in it.
    /// </summary>
    [Fact]
    public void TheAgentTabIsReadOnly()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var start = window.IndexOf("Strings[Settings.Tab.Agent]", StringComparison.Ordinal);
        var end = window.IndexOf("Strings[Settings.Tab.About]", start, StringComparison.Ordinal);
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

        Assert.DoesNotContain("Command=", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("<Button", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("<TextBox", tab, StringComparison.Ordinal);
    }

    /// <summary>
    /// About states what this build is and links to one fixed page. No logo and no licence heading:
    /// neither was asked for, and both were explicitly excluded.
    /// </summary>
    [Fact]
    public void AboutReportsTheBuildAndLinksToOneFixedPage()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");

        var about = window[window.IndexOf("Strings[Settings.Tab.About]", StringComparison.Ordinal)..];

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
    /// The gear announces itself in both languages, and the name it gives is the one the tooltip
    /// shows - a button whose accessible name differed from its tooltip would be two controls to
    /// anyone reading it through a screen reader.
    /// </summary>
    [Fact]
    public void TheGearIsNamedInBothLanguages()
    {
        var window = T42UnifiedHostTests.Read("src/NutManager.Agent.Config/Views/MainWindow.axaml");
        var strings = T42UnifiedHostTests.Read(
            "src/NutManager.Agent.Config/Localization/AgentConfigStrings.cs");

        var gear = window.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal);
        var closing = window.IndexOf("</Button>", gear, StringComparison.Ordinal);
        var markup = window[gear..closing];

        Assert.Contains("ToolTip.Tip=\"{Binding Strings[Settings.Title]}\"", markup, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding Strings[Settings.Title]}\"",
            markup,
            StringComparison.Ordinal);

        Assert.Contains("[\"Settings.Title\"] = \"Configurações\"", strings, StringComparison.Ordinal);
        Assert.Contains("[\"Settings.Title\"] = \"Settings\"", strings, StringComparison.Ordinal);
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
}
