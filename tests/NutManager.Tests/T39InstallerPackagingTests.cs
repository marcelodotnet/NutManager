using System.Text.RegularExpressions;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// Guards the installer authoring.
///
/// These assert over source text, and that is a deliberate choice rather than a shortcut. The
/// invariants that matter here are declarations — which service is named, which account it runs
/// under, which files the package claims to own — and a declaration is exactly what a Windows
/// Installer package is. Building an MSI to read back what the .wxs already states would make the
/// suite depend on the WiX toolchain being installed, turning a missing tool into a test failure and
/// making the solution ungatable on a machine that only wants to run the tests.
///
/// What this cannot catch is the engine behaving differently from the manifest. That belongs to the
/// manual acceptance recorded in the packaging documentation.
/// </summary>
public sealed class T39InstallerPackagingTests
{
    // ---------------------------------------------------------------- the NUT boundary

    [Fact]
    public void NoInstallerSourceMentionsANutConfigurationFile()
    {
        // The installers package NutManager. NUT's configuration belongs to NUT, and the safe-write
        // pipeline is the only thing in this product allowed to touch it. An installer that so much as
        // names one of these files has grown a second configuration writer with none of the pipeline's
        // backup, validation or rollback behind it.
        string[] nutFiles = ["nut.conf", "ups.conf", "upsd.conf", "upsd.users", "upsmon.conf"];

        foreach (var (path, source) in InstallerSources())
        {
            foreach (var file in nutFiles)
            {
                Assert.False(
                    source.Contains(file, StringComparison.OrdinalIgnoreCase),
                    $"{path} names {file}. No installer may read, write or remove NUT configuration.");
            }
        }
    }

    [Fact]
    public void NoInstallerSourceRunsANutExecutableOrOpensADevice()
    {
        // Installing the agent must not become a way to reach hardware. The passive-inspection boundary
        // from T38 says nothing opens a port or runs a driver, and an installer custom action would be a
        // way around it that no runtime test would ever see.
        string[] forbidden = ["upsdrvctl", "nutdrv_qx", "usbhid-ups", "upsd.exe", "upsmon.exe"];

        foreach (var (path, source) in InstallerSources())
        {
            foreach (var token in forbidden)
            {
                Assert.False(
                    source.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"{path} names {token}. Installers never run a NUT executable.");
            }
        }
    }

    [Fact]
    public void TheAgentPackageControlsOnlyItsOwnService()
    {
        // The failure this exists to prevent is an upgrade stopping the wrong service. Every ServiceControl
        // and ServiceInstall in the agent package must name the agent's own service and nothing else — no
        // wildcard, no discovered name, and above all nothing belonging to NUT.
        var source = Read("installer/Agent/Package.wxs");

        var names = Regex.Matches(source, @"<Service(?:Install|Control)\b[^>]*?\bName=""([^""]+)""", RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.Equal("$(AgentServiceName)", name));

        // And that define resolves to the agent's own service, not to something discovered at install time.
        Assert.Contains(
            @"<?define AgentServiceName = ""NutManagerAgent"" ?>",
            Read("installer/Common/Product.wxi"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheAgentPackageInstallsAService()
    {
        // The desktop application is not a service and must never quietly become one, which is also what
        // stops the desktop installer from being a second way to deploy the agent.
        Assert.DoesNotContain("ServiceInstall", Read("installer/Desktop/Package.wxs"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the service itself

    [Fact]
    public void TheAgentServiceRunsAsLocalSystemAndStartsAutomatically()
    {
        // LocalSystem is what the authorization model assumes: the agent decides a caller's rights by
        // group membership rather than inheriting whoever started it. Installing it under another account
        // is a different security posture than the one that was reviewed.
        var install = Regex.Match(Read("installer/Agent/Package.wxs"), @"<ServiceInstall\b.*?/>", RegexOptions.Singleline);
        Assert.True(install.Success, "The agent package no longer declares a service.");

        Assert.Contains(@"Account=""LocalSystem""", install.Value, StringComparison.Ordinal);
        Assert.Contains(@"Start=""auto""", install.Value, StringComparison.Ordinal);
        Assert.Contains(@"Type=""ownProcess""", install.Value, StringComparison.Ordinal);

        // A password attribute on a LocalSystem service would mean an account was introduced without any
        // of these assertions changing.
        Assert.DoesNotContain("Password=", install.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoInstallerShellsOutToRegisterAnything()
    {
        // WiX declares the service and the engine registers it. A custom action reaching for sc.exe,
        // PowerShell or cmd would put privileged registration back into imperative code, which is the
        // thing choosing WiX was meant to avoid.
        string[] forbidden = ["sc.exe", "powershell", "cmd.exe", "CustomAction", "netsh", "certutil"];

        foreach (var (path, source) in InstallerSources())
        {
            foreach (var token in forbidden)
            {
                Assert.False(
                    source.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"{path} contains '{token}'. Installation stays declarative.");
            }
        }
    }

    // ---------------------------------------------------------------- what survives

    [Fact]
    public void TheAgentPackageNeverDeclaresItsConfigurationFile()
    {
        // agent.json survives upgrade and uninstall because the package does not own it. That is the whole
        // mechanism: a file the manifest never declares is one the engine has no way to schedule for
        // removal or replacement. Adding it as a component, even in order to "preserve" it, breaks that.
        Assert.DoesNotContain("agent.json", WithoutComments(Read("installer/Agent/Package.wxs")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDesktopPackageNeverDeclaresUserData()
    {
        // Settings and profiles live under the user profile, and the credential entries live in the Windows
        // credential store. None of them may appear as a component, because a component is precisely what
        // uninstall removes.
        string[] userData = ["settings.json", "managed-servers.json", "Credential", "AppData"];
        var source = WithoutComments(Read("installer/Desktop/Package.wxs"));

        foreach (var token in userData)
        {
            Assert.False(
                source.Contains(token, StringComparison.OrdinalIgnoreCase),
                $"The desktop package names '{token}'. User data is never a component.");
        }
    }

    [Fact]
    public void UninstallRemovesNothingTheInstallerDidNotCreate()
    {
        // RemoveFile and RemoveRegistryKey can delete things a package never installed. Neither belongs in
        // either product: uninstall removes components, and a component is by definition something the
        // installer put there. RemoveFolder is allowed — it removes only a folder this package created.
        string[] forbidden = ["RemoveFile", "RemoveRegistryKey", "RemoveRegistryValue"];

        foreach (var (path, source) in InstallerSources())
        {
            foreach (var token in forbidden)
            {
                Assert.False(
                    source.Contains(token, StringComparison.Ordinal),
                    $"{path} uses {token}. Uninstall removes only what the installer owns.");
            }
        }
    }

    // ---------------------------------------------------------------- identity and versioning

    [Fact]
    public void BothProductsTakeTheirVersionFromTheOneSource()
    {
        // An installer whose ProductVersion disagrees with the assembly it ships is an upgrade that either
        // refuses to run or silently does nothing, and from outside those look the same. Neither package
        // may carry a literal version.
        foreach (var (path, source) in InstallerSources())
        {
            // ExePackagePayload states the third-party prerequisite's own version, which is a fact
            // about Microsoft's file rather than about this product. It is still not a literal: it
            // comes from the pinned define that sits beside the URL and hash it belongs with.
            var productVersions = Regex.Matches(source, @"\bVersion=""([^""]+)""")
                .Select(match => match.Groups[1].Value)
                .Where(version => !version.StartsWith("$(AspNetCoreRuntimeVersion)", StringComparison.Ordinal));

            Assert.All(productVersions, version => Assert.Equal("$(Version)", version));
        }

        Assert.Matches(@"<NutManagerVersion[^>]*>\d+\.\d+\.\d+</NutManagerVersion>", Read("Directory.Build.props"));
    }

    [Fact]
    public void EveryUpgradeCodeIsDistinct()
    {
        // Two products sharing an upgrade code makes each one's install look like the other's upgrade, and
        // uninstalling either starts removing the other. A bundle sharing its package's code has the same
        // effect between the bundle and what it chains.
        var codes = Regex.Matches(Read("installer/Common/Product.wxi"), @"UpgradeCode = ""(\{[0-9A-Fa-f-]+\})""")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(4, codes.Length);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BothPackagesRefuseADowngradeAndInstallPerMachine()
    {
        foreach (var (path, source) in new[]
                 {
                     ("installer/Desktop/Package.wxs", Read("installer/Desktop/Package.wxs")),
                     ("installer/Agent/Package.wxs", Read("installer/Agent/Package.wxs"))
                 })
        {
            Assert.Contains("<MajorUpgrade", source, StringComparison.Ordinal);
            Assert.Contains("DowngradeErrorMessage=", source, StringComparison.Ordinal);
            Assert.Contains(@"Scope=""perMachine""", source, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- secrets and infrastructure

    [Fact]
    public void NoInstallerSourceCarriesASecret()
    {
        // Installer authoring is committed, shipped inside the package and readable by anyone holding the
        // .exe. Nothing that authenticates anything may live here.
        string[] forbidden = ["password=", ".pfx", "privatekey", "private-key", "thumbprint", "apikey", "secret="];

        foreach (var (path, source) in InstallerSources())
        {
            foreach (var token in forbidden)
            {
                Assert.False(
                    source.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"{path} appears to contain '{token}'.");
            }
        }
    }

    [Fact]
    public void NoInstallerTouchesHttpsInfrastructure()
    {
        // HTTPS stays an explicit administrative decision. An installer that creates a binding, reserves a
        // URL or opens a port turns an opt-in transport into one nobody chose.
        string[] forbidden = ["http.sys", "sslcert", "urlacl", "netfirewall", "FirewallException"];

        foreach (var (path, source) in InstallerSources())
        {
            foreach (var token in forbidden)
            {
                Assert.False(
                    source.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"{path} references '{token}'. HTTPS setup is not automated.");
            }
        }
    }

    [Fact]
    public void NoInstallerCreatesAnAuthorizationGroup()
    {
        // The agent authorizes by membership of NutManager Operators. An installer that created the group
        // would be deciding who may control a service, and on a domain controller it would be changing the
        // directory as a side effect of running setup.
        foreach (var (path, source) in InstallerSources())
        {
            Assert.False(
                source.Contains("<Group", StringComparison.Ordinal) || source.Contains("CreateGroup", StringComparison.Ordinal),
                $"{path} creates a group. Authorization membership is an administrator's decision.");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static readonly string[] Sources =
    [
        "installer/Desktop/Package.wxs",
        "installer/Desktop/Bundle.wxs",
        "installer/Agent/Package.wxs",
        "installer/Agent/Bundle.wxs"
    ];

    // ---------------------------------------------------------------- branding

    [Fact]
    public void BothBundlesUseTheHighResolutionArtworkAndKeepTheIconWhereAnIconBelongs()
    {
        // The .ico tops out at 256px, and scaling its largest frame to fill the header is what made
        // the previous bundles look pixellated. The icon keeps the jobs an icon is for.
        var script = Read("scripts/build-release.ps1");

        Assert.Contains("Assets\\Branding\\NutManager.png", script, StringComparison.Ordinal);
        Assert.Contains("-d \"BrandingLogo=$brandingLogo\"", script, StringComparison.Ordinal);

        foreach (var bundle in new[] { "installer/Desktop/Bundle.wxs", "installer/Agent/Bundle.wxs" })
        {
            var source = WithoutComments(Read(bundle));

            Assert.Contains("LogoFile=\"$(BrandingLogo)\"", source, StringComparison.Ordinal);
            Assert.Contains("IconSourceFile=\"$(BrandingIcon)\"", source, StringComparison.Ordinal);

            // The theme artwork must never be the icon.
            Assert.DoesNotContain("LogoFile=\"$(BrandingIcon)\"", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EachBundleCarriesItsOwnBrandedThemeAndKeepsTheVersionVisible()
    {
        var desktop = WithoutComments(Read("installer/Desktop/Bundle.wxs"));
        var agent = WithoutComments(Read("installer/Agent/Bundle.wxs"));

        foreach (var source in new[] { desktop, agent })
        {
            Assert.Contains("ThemeFile=\"$(ThemeFile)\"", source, StringComparison.Ordinal);
            Assert.Contains("LocalizationFile=\"$(ThemeLocalization)\"", source, StringComparison.Ordinal);
            Assert.Contains("ShowVersion=\"yes\"", source, StringComparison.Ordinal);

            // WixStdBA, not a bespoke bootstrapper application carrying its own executable into an
            // elevated install.
            Assert.Contains("bal:WixStandardBootstrapperApplication", source, StringComparison.Ordinal);
        }

        var script = Read("scripts/build-release.ps1");
        Assert.Contains("DesktopTheme.xml", script, StringComparison.Ordinal);
        Assert.Contains("AgentTheme.xml", script, StringComparison.Ordinal);

        foreach (var theme in new[] { "installer/Common/Theme/DesktopTheme.xml", "installer/Common/Theme/AgentTheme.xml" })
        {
            var source = Read(theme);
            Assert.Contains("ImageFile=\"logo.png\"", source, StringComparison.Ordinal);
            Assert.Contains("#(loc.InstallVersion)", source, StringComparison.Ordinal);

            // Every one of these is a control WixStdBA binds by name. Losing one does not move it.
            foreach (var control in new[] { "EulaRichedit", "EulaAcceptCheckbox", "InstallButton", "OverallCalculatedProgressbar" })
            {
                Assert.Contains($"Name=\"{control}\"", source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheDesktopAndTheAgentDescribeThemselvesAsDifferentThings()
    {
        // Someone must not install the server service believing it is the application with the
        // windows and the charts.
        foreach (var culture in new[] { "pt-BR", "en-US" })
        {
            var desktop = Read($"installer/Common/Theme/Desktop.{culture}.wxl");
            var agent = Read($"installer/Common/Theme/Agent.{culture}.wxl");

            Assert.NotEqual(StringValue(desktop, "ProductTagline"), StringValue(agent, "ProductTagline"));
            Assert.NotEqual(StringValue(desktop, "ProductKind"), StringValue(agent, "ProductKind"));
            Assert.NotEqual(StringValue(desktop, "InstallInstallButton"), StringValue(agent, "InstallInstallButton"));
        }

        Assert.Equal("Aplicativo de administração", StringValue(Read("installer/Common/Theme/Desktop.pt-BR.wxl"), "ProductKind"));
        Assert.Equal("Componente de servidor", StringValue(Read("installer/Common/Theme/Agent.pt-BR.wxl"), "ProductKind"));
    }

    [Fact]
    public void TheInstallerStringsHaveExactParityBetweenTheTwoOfficialCultures()
    {
        foreach (var product in new[] { "Desktop", "Agent" })
        {
            var pt = StringIds(Read($"installer/Common/Theme/{product}.pt-BR.wxl"));
            var en = StringIds(Read($"installer/Common/Theme/{product}.en-US.wxl"));

            Assert.Equal(pt.Order(), en.Order());
            Assert.NotEmpty(pt);
        }
    }

    // ---------------------------------------------------------------- terms of use

    [Fact]
    public void TheTermsAreEmbeddedRatherThanFetchedAndAcceptanceGatesInstall()
    {
        foreach (var bundle in new[] { "installer/Desktop/Bundle.wxs", "installer/Agent/Bundle.wxs" })
        {
            var source = WithoutComments(Read(bundle));

            // A local RTF, not a URL. An operator on an isolated server must be able to read what
            // they are accepting.
            Assert.Contains("LicenseFile=\"$(TermsFile)\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("LicenseUrl=", source, StringComparison.Ordinal);

            // rtfLicense is the WixStdBA behaviour that requires acceptance before Install enables.
            Assert.Contains("Theme=\"rtfLicense\"", source, StringComparison.Ordinal);
        }

        foreach (var theme in new[] { "installer/Common/Theme/DesktopTheme.xml", "installer/Common/Theme/AgentTheme.xml" })
        {
            Assert.Contains("Name=\"EulaAcceptCheckbox\"", Read(theme), StringComparison.Ordinal);
        }

        // The Agent restates the gate in its own EnableCondition, so a condition meant for the
        // runtime cannot silently replace the acceptance requirement.
        Assert.Contains("AND EulaAcceptCheckbox", Read("installer/Common/Theme/AgentTheme.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTermsAreNotPresentedAsTheGplAndSayTheyDoNotRestrictIt()
    {
        var terms = Read("docs/TERMS-OF-USE.md");

        Assert.Contains(
            "não substituem, restringem ou modificam os direitos concedidos pela GPL v2.0",
            terms,
            StringComparison.Ordinal);
        Assert.Contains(
            "sem garantia de funcionamento ininterrupto, ausência de erros ou adequação a uma finalidade específica",
            terms,
            StringComparison.Ordinal);
        Assert.Contains("Marcelo Pacheco", terms, StringComparison.Ordinal);
        Assert.Contains("@marcelodotnet", terms, StringComparison.Ordinal);

        // The acceptance line names the Terms; the GPL is stated separately and never as the thing
        // being accepted. One checkbox covering both would imply the GPL is a condition of
        // installing, which inverts what the GPL is.
        foreach (var (culture, accepted) in new[] { ("pt-BR", "Termos de Uso"), ("en-US", "Terms of Use") })
        {
            foreach (var product in new[] { "Desktop", "Agent" })
            {
                var strings = Read($"installer/Common/Theme/{product}.{culture}.wxl");

                var accept = StringValue(strings, "InstallAcceptCheckbox");
                Assert.Contains(accepted, accept, StringComparison.Ordinal);
                Assert.DoesNotContain("GPL", accept, StringComparison.OrdinalIgnoreCase);

                Assert.Contains("GNU GPL v2.0", StringValue(strings, "GplNotice"), StringComparison.Ordinal);
            }
        }

        // The GPL itself stays in the repository.
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), "LICENSE")));
    }

    [Fact]
    public void TheGeneratedTermsCarryTheWholeDocumentAndNoMaintenanceNotes()
    {
        foreach (var (culture, source) in new[]
                 {
                     ("pt-BR", "docs/TERMS-OF-USE.md"),
                     ("en-US", "docs/TERMS-OF-USE.en-US.md")
                 })
        {
            var markdown = Read(source);
            var rtf = Read($"installer/Common/Terms/Terms.{culture}.rtf");

            Assert.StartsWith("{\\rtf1", rtf, StringComparison.Ordinal);

            // All twenty sections survive the conversion. A licence pane that silently truncates is
            // the operator accepting less than was published.
            Assert.Equal(20, Regex.Matches(markdown, @"^## (\d+)\. ", RegexOptions.Multiline).Count);
            Assert.Equal(20, Regex.Matches(rtf, @"\\fs24 \d+\.").Count);

            // The repository's own notes to itself are not part of the legal text.
            Assert.DoesNotContain("PENDENTE PARA v1.0.1", rtf, StringComparison.Ordinal);
            Assert.DoesNotContain("PENDING FOR v1.0.1", rtf, StringComparison.Ordinal);
            Assert.DoesNotContain("build-terms-rtf", rtf, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NothingInstallerVisiblePointsAtTheRetiredGitHubAccount()
    {
        // The account was renamed. GitHub redirects the old name only until someone else claims it.
        //
        // Comments are stripped for the same reason the rest of this suite strips them: the note in
        // Product.wxi recording why nothing points at the old account has to name the old account,
        // and scanning prose would make documenting the decision the way to fail its own test.
        foreach (var path in Sources.Concat(["installer/Common/Product.wxi"]))
        {
            Assert.DoesNotContain("Marcelo-PX", WithoutComments(Read(path)), StringComparison.OrdinalIgnoreCase);
        }

        foreach (var path in new[] { "docs/TERMS-OF-USE.md", "docs/TERMS-OF-USE.en-US.md" })
        {
            Assert.DoesNotContain("Marcelo-PX", Read(path), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("https://github.com/marcelodotnet/NutManager", Read("installer/Common/Product.wxi"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- runtime deployment model

    [Fact]
    public void TheDesktopStaysSelfContainedAndTheAgentDoesNot()
    {
        var script = Read("scripts/build-release.ps1");

        Assert.Contains("-Output $desktopPublish -SelfContained $true", script, StringComparison.Ordinal);
        Assert.Contains("-Output $agentPublish -SelfContained $false", script, StringComparison.Ordinal);

        // Mandatory with no default, so the two products cannot quietly become the same again.
        Assert.Contains("[Parameter(Mandatory)] [bool] $SelfContained", script, StringComparison.Ordinal);

        // And the build proves the agent payload rather than inferring it from a size.
        Assert.Contains("privateRuntimeMarkers", script, StringComparison.Ordinal);
        Assert.Contains("hostpolicy.dll", script, StringComparison.Ordinal);

        // The Agent needs the ASP.NET Core shared framework, which is why aspnet is the runtime type
        // the bundle searches for.
        Assert.Contains(
            "<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />",
            Read("src/NutManager.Agent/NutManager.Agent.csproj"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheAgentBundleCarriesARuntimePrerequisite()
    {
        var desktop = WithoutComments(Read("installer/Desktop/Bundle.wxs"));

        // A desktop user downloads one file and runs it. No prompt, no download, no prerequisite.
        Assert.DoesNotContain("DotNetCoreSearch", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("ExePackage", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("aspnetcore", desktop, StringComparison.OrdinalIgnoreCase);

        var agent = WithoutComments(Read("installer/Agent/Bundle.wxs"));
        Assert.Contains("netfx:DotNetCoreSearch", agent, StringComparison.Ordinal);
        Assert.Contains("RuntimeType=\"aspnet\"", agent, StringComparison.Ordinal);
        Assert.Contains("Platform=\"x64\"", agent, StringComparison.Ordinal);
        Assert.Contains("MajorVersion=\"$(AspNetCoreMajorVersion)\"", agent, StringComparison.Ordinal);
        Assert.Equal("10", Define("AspNetCoreMajorVersion"));
    }

    [Fact]
    public void TheRuntimePackageComesOnlyFromMicrosoftAndIsVerifiedBeforeItRuns()
    {
        var product = Read("installer/Common/Product.wxi");
        var url = Define("AspNetCoreRuntimeUrl");

        Assert.StartsWith("https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/", url, StringComparison.Ordinal);
        Assert.EndsWith("aspnetcore-runtime-10.0.11-win-x64.exe", url, StringComparison.Ordinal);

        // Verified before it is run with elevation: a substituted or corrupted download fails.
        Assert.Equal(128, Define("AspNetCoreRuntimeHash").Length);
        Assert.Equal("11262944", Define("AspNetCoreRuntimeSize"));

        var agent = WithoutComments(Read("installer/Agent/Bundle.wxs"));
        Assert.Contains("DownloadUrl=\"$(AspNetCoreRuntimeUrl)\"", agent, StringComparison.Ordinal);
        Assert.Contains("Hash=\"$(AspNetCoreRuntimeHash)\"", agent, StringComparison.Ordinal);
        Assert.Contains("Size=\"$(AspNetCoreRuntimeSize)\"", agent, StringComparison.Ordinal);

        // One fixed package, not a mechanism for fetching packages: a single ExePackage and a single
        // Microsoft address in the whole authoring. No hosting bundle - the Agent does not use IIS.
        Assert.Single(Regex.Matches(agent, "<ExePackage "));
        Assert.Single(Regex.Matches(product, @"https://builds\.dotnet\.microsoft\.com"));
        Assert.DoesNotContain("hosting", product, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRuntimeIsNotPlannedWhenOneIsAlreadyPresentAndIsNeverRemovedWithTheAgent()
    {
        var agent = WithoutComments(Read("installer/Agent/Bundle.wxs"));

        // Compatibility-oriented: any serviced 10.x satisfies it, so a machine on 10.0.7 downloads
        // nothing. Pinning detection to one patch would reinstall a runtime that already works.
        Assert.Contains(
            "DetectCondition=\"AspNetCoreRuntimeVersion &gt;= v$(AspNetCoreMajorVersion).0.0\"",
            agent,
            StringComparison.Ordinal);

        // Machine-shared Microsoft component: removing the Agent must not take it from whatever else
        // on the server depends on it.
        Assert.Contains("Permanent=\"yes\"", agent, StringComparison.Ordinal);
    }

    [Fact]
    public void DecliningTheRuntimeCannotProduceAnAgentThatWillNotStart()
    {
        var agent = WithoutComments(Read("installer/Agent/Bundle.wxs"));

        // The theme disables the button, but /quiet has no buttons. The chain has to refuse too, or
        // one command line produces a registered service that cannot start.
        var msi = Regex.Match(agent, "<MsiPackage .*?/>", RegexOptions.Singleline).Value;
        Assert.Contains(
            "InstallCondition=\"AspNetCoreRuntimeVersion &gt;= v$(AspNetCoreMajorVersion).0.0 OR InstallAspNetRuntime = 1\"",
            msi,
            StringComparison.Ordinal);

        // Overridable so an administrator can refuse deliberately in an unattended install.
        Assert.Contains(
            "<Variable Name=\"InstallAspNetRuntime\" Type=\"numeric\" Value=\"1\" bal:Overridable=\"yes\" />",
            agent,
            StringComparison.Ordinal);

        // And the interactive path says why the button is disabled rather than just greying it out.
        var theme = Read("installer/Common/Theme/AgentTheme.xml");
        Assert.Contains("#(loc.RuntimeBlockedMessage)", theme, StringComparison.Ordinal);
        Assert.Contains(
            "EnableCondition=\"(AspNetCoreRuntimeVersion &gt;= v10.0.0 OR InstallAspNetRuntime) AND EulaAcceptCheckbox\"",
            theme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoGenericDownloaderOrArbitraryExecutableWasIntroduced()
    {
        foreach (var (path, source) in InstallerSources())
        {
            // The one prerequisite is named and fixed. A URL assembled at runtime, or one arriving
            // from a variable, would make this a general-purpose installer of other people's code.
            Assert.DoesNotContain("[DownloadUrl]", source, StringComparison.Ordinal);

            foreach (var shell in new[] { "cmd.exe", "powershell", "netsh", "net.exe", "regsvr32" })
            {
                Assert.DoesNotContain(shell, source, StringComparison.OrdinalIgnoreCase);
            }

            // Every http(s) address in the authoring is either an XML namespace or the one Microsoft
            // download. Anything else is a destination nobody reviewed.
            foreach (Match address in Regex.Matches(source, @"https?://[^""\s]+"))
            {
                var value = address.Value;
                var allowed =
                    value.StartsWith("http://wixtoolset.org/", StringComparison.Ordinal) ||
                    value.StartsWith("http://schemas.microsoft.com/", StringComparison.Ordinal) ||
                    value.StartsWith("https://github.com/marcelodotnet/", StringComparison.Ordinal) ||
                    value.StartsWith("https://builds.dotnet.microsoft.com/dotnet/aspnetcore/", StringComparison.Ordinal);

                Assert.True(allowed, $"Unreviewed address in {path}: {value}");
            }
        }
    }

    private static string Define(string name) =>
        Regex.Match(
            Read("installer/Common/Product.wxi"),
            $@"<\?define\s+{Regex.Escape(name)}\s*=\s*""([^""]*)""\s*\?>").Groups[1].Value;

    private static string StringValue(string localization, string id) =>
        Regex.Match(localization, $@"<String\s+Id=""{Regex.Escape(id)}""\s+Value=""([^""]*)""").Groups[1].Value;

    private static IEnumerable<string> StringIds(string localization) =>
        Regex.Matches(localization, @"<String\s+Id=""([^""]+)""").Select(match => match.Groups[1].Value);

    /// <summary>
    /// The authoring with its comments removed.
    ///
    /// Every one of these invariants is about what the package declares, and the comments explaining
    /// why something is absent name the very thing being forbidden — the note recording that agent.json
    /// is deliberately not a component contains the string "agent.json". Scanning the prose would make
    /// documenting a boundary the way to fail its own test, which teaches the next person to delete the
    /// explanation rather than keep it.
    /// </summary>
    private static IEnumerable<(string Path, string Source)> InstallerSources() =>
        Sources.Select(path => (path, WithoutComments(Read(path))));

    private static string WithoutComments(string source) =>
        Regex.Replace(source, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NutManager.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
