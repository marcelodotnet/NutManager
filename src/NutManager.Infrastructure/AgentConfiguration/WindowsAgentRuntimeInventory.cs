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
            FindSharedFramework(DotNetRuntimeName),
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
    private static string? FindSharedFramework(string frameworkName)
    {
        try
        {
            var root = ResolveDotNetRoot();
            if (root is null) return null;

            var directory = Path.Combine(root, "shared", frameworkName);
            if (!Directory.Exists(directory)) return null;

            Version? newest = null;

            foreach (var candidate in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(candidate);

                // Preview and release-candidate directories carry a suffix after a hyphen. The numeric
                // part is what decides compatibility.
                var numeric = name.Split('-', 2)[0];

                if (!Version.TryParse(numeric, out var version)) continue;
                if (version.Major != RequiredMajorVersion) continue;
                if (newest is null || version > newest) newest = version;
            }

            return newest?.ToString();
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
    private static string? ResolveDotNetRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) return null;

        var root = Path.Combine(programFiles, "dotnet");
        return Directory.Exists(root) ? root : null;
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
