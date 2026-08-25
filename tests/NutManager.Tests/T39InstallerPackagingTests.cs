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
            var versions = Regex.Matches(source, @"\bVersion=""([^""]+)""")
                .Select(match => match.Groups[1].Value);

            Assert.All(versions, version => Assert.Equal("$(Version)", version));
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
