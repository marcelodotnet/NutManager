using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NutManager.Core.Agent;
using NutManager.Infrastructure.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// What this machine actually has, for the diagnostics view.
///
/// Every member reads. Nothing here installs a runtime, registers a log source or touches NUT — the
/// diagnostics view reports, and the sections above it are where anything changes.
///
/// NUT detection is delegated to <see cref="WindowsNutServiceTargetResolver"/> rather than repeated.
/// That resolver applies the rule that matters: a service counts as NUT's only if its binary lives
/// inside a detected NUT installation, so one that merely borrowed the name is rejected.
/// Re-implementing a looser version here would mean this window and the agent disagreeing about
/// whether NUT is present on the same machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentRuntimeInventory : IAgentRuntimeInventory
{
    private const string EventLogSourceKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\NutManager Agent";

    private const string DotNetRuntimeName = "Microsoft.NETCore.App";
    private const string AspNetCoreRuntimeName = "Microsoft.AspNetCore.App";

    /// <summary>The major version both the agent and this utility are built for.</summary>
    private const int RequiredMajorVersion = 10;

    private readonly INutServiceTargetResolver _nutResolver;

    public WindowsAgentRuntimeInventory()
        : this(new WindowsNutServiceTargetResolver())
    {
    }

    internal WindowsAgentRuntimeInventory(INutServiceTargetResolver nutResolver)
    {
        ArgumentNullException.ThrowIfNull(nutResolver);
        _nutResolver = nutResolver;
    }

    public async Task<AgentRuntimeInventorySnapshot> DescribeAsync(CancellationToken cancellationToken)
    {
        var nut = await DescribeNutAsync(cancellationToken).ConfigureAwait(false);

        return new AgentRuntimeInventorySnapshot(
            DescribeRunningRuntime(),
            FindSharedFramework(AspNetCoreRuntimeName),
            IsEventLogSourceRegistered(),
            nut.Detected,
            nut.Detail);
    }

    private async Task<(bool Detected, string? Detail)> DescribeNutAsync(CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _nutResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);

            return resolution.Status switch
            {
                NutServiceTargetStatus.Resolved when resolution.Target is { } target =>
                    (true, target.ServiceName),

                // Several plausible services is not "no NUT", and reporting it as such would send an
                // administrator looking for an installation that is sitting right there. It is an
                // ambiguity only they can settle.
                NutServiceTargetStatus.Ambiguous =>
                    (false, resolution.Detail ?? "More than one service looks like NUT; none was chosen."),

                NutServiceTargetStatus.ValidationFailed =>
                    (false, resolution.Detail ?? "A NUT service was found but no longer satisfies the association rules."),

                NutServiceTargetStatus.QueryFailed =>
                    (false, resolution.Detail ?? "The service control manager could not be queried."),

                _ => (false, null),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (false, $"NUT could not be detected ({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// The newest installed version of a shared framework, read from the layout the .NET host itself
    /// uses: one directory per version under <c>dotnet\shared\&lt;framework&gt;</c>.
    ///
    /// Read from disk rather than by running <c>dotnet</c> with an argument, because this product does
    /// not start processes to answer questions. The directory layout is the documented installation
    /// shape and the one the host resolves against at startup, so it is the same answer arrived at
    /// without a shell.
    /// </summary>
    /// <summary>
    /// The .NET runtime this process is running on, asked of the runtime itself.
    ///
    /// This used to be a directory scan under Program Files, and on a server with .NET plainly
    /// installed it reported nothing - which the screen showed as an unknown runtime while running on
    /// that very runtime. A process cannot be wrong about its own version, and no install location or
    /// environment variable can make it wrong, so that is what is asked.
    ///
    /// FrameworkDescription first because it carries the servicing version as shipped (".NET 10.0.11");
    /// Environment.Version is the same number through a different door and covers a description that
    /// does not parse.
    /// </summary>
    private static string? DescribeRunningRuntime()
    {
        var description = RuntimeInformation.FrameworkDescription;
        var numeric = new string(description.SkipWhile(character => !char.IsDigit(character)).ToArray());

        if (Version.TryParse(numeric.Split('-', 2)[0], out var described)) return described.ToString();

        var version = Environment.Version;
        return version.Major >= RequiredMajorVersion ? version.ToString() : null;
    }

    /// <summary>
    /// The newest compatible build of a shared framework this machine has installed.
    ///
    /// ASP.NET Core cannot be read off the running process the way the runtime above can: the
    /// configuration window never loads it, and the version that matters is the one the service will
    /// resolve when it starts. So this still reads the disk - but from every place the framework can
    /// legitimately be, rather than from one guess that fails silently.
    /// </summary>
    private static string? FindSharedFramework(string frameworkName)
    {
        try
        {
            foreach (var root in DotNetRoots())
            {
                var directory = Path.Combine(root, "shared", frameworkName);
                if (!Directory.Exists(directory)) continue;

                Version? newest = null;

                foreach (var candidate in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(candidate);

                    // Preview and release-candidate directories carry a suffix after a hyphen. The
                    // numeric part is what decides compatibility.
                    var numeric = name.Split('-', 2)[0];

                    if (!Version.TryParse(numeric, out var version)) continue;
                    if (version.Major != RequiredMajorVersion) continue;
                    if (newest is null || version > newest) newest = version;
                }

                if (newest is not null) return newest.ToString();
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Where .NET is installed. DOTNET_ROOT first, because a machine that sets it means it; otherwise
    /// the default 64-bit location, which is where the runtime installer this product offers puts it.
    /// </summary>
    /// <summary>
    /// Every place this machine may keep its shared frameworks, most authoritative first.
    ///
    /// The installation this process is running out of comes first, because it is the one place that
    /// cannot be a guess: a framework-dependent app loads its runtime from
    /// {root}/shared/Microsoft.NETCore.App/{version}, so walking three levels up from the assembly
    /// that defines object lands on the root by construction.
    ///
    /// The previous version returned the first candidate that merely existed and gave up if the
    /// framework was not under it. DOTNET_ROOT pointing at a directory with no shared frameworks -
    /// which is a normal thing for it to do - was therefore enough to make an installed runtime
    /// invisible. These are candidates now, and the caller keeps looking.
    /// </summary>
    private static IEnumerable<string> DotNetRoots()
    {
        var running = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(running))
        {
            var root = Path.GetFullPath(Path.Combine(running, "..", "..", ".."));
            if (Directory.Exists(root)) yield return root;
        }

        var configured = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) yield return configured;

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            var programFiles = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(programFiles)) continue;

            var root = Path.Combine(programFiles, "dotnet");
            if (Directory.Exists(root)) yield return root;
        }
    }

    /// <summary>
    /// Whether the installer's Event Log source is registered.
    ///
    /// It matters because the agent refuses to run without a usable audit sink: a missing source means
    /// a service that will not start, for a reason that is not obvious from the outside.
    /// </summary>
    private static bool IsEventLogSourceRegistered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EventLogSourceKey, writable: false);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
