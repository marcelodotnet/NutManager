using NutManager.Core.Configuration;

namespace NutManager.Core.Administration;

/// <summary>
/// Reads the UPS sections of a <c>ups.conf</c> that belongs to another machine.
///
/// It exists because the local interpreter answers questions that only make sense locally. Resolving
/// a driver executable, deciding whether its path is inside a trusted installation and asking whether
/// that image is currently running are all statements about the machine doing the asking, and
/// applying them to a remote document would produce confident answers about the wrong computer. So
/// the fields that cannot be established from here are reported as not established rather than
/// filled in: the executable is <see cref="NutDriverExecutableState.NotApplicable"/> and the driver's
/// runtime state is <see cref="NutDriverRuntimeState.Unknown"/>.
///
/// What it does establish is the one relationship this view exists for — the configured port against
/// the ports the remote machine actually reported — and it never writes anything. The document is
/// read through the configuration transport that already owns it; no second reader and no writer of
/// any kind is introduced here.
/// </summary>
public static class NutRemoteConfiguredDriverReader
{
    /// <summary>
    /// Interprets every UPS section for presentation.
    /// </summary>
    /// <param name="document">A loaded <c>ups.conf</c>. It is read and never modified.</param>
    /// <param name="detectedPorts">
    /// What the remote machine reported, or null when it could not be asked. Null is not an empty
    /// list: with no port list, a configured port is reported as raising no contradiction, exactly as
    /// a non-COM value such as <c>auto</c> is. An agent that is unreachable must never make a port
    /// that is plugged in read as missing.
    /// </param>
    public static IReadOnlyList<NutConfiguredDriver> Read(
        NutConfigurationDocument document,
        IReadOnlyList<NutComPortInfo>? detectedPorts)
    {
        ArgumentNullException.ThrowIfNull(document);

        var driverPath = document.Nodes
            .OfType<NutConfigurationAssignmentNode>()
            .FirstOrDefault(node => node.SectionName is null &&
                string.Equals(node.Name, "driverpath", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var present = detectedPorts?
            .Select(port => port.PortName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return document.Sections.Select(section =>
        {
            string? Get(string name) => document
                .FindAssignments(name, section.Name, StringComparison.OrdinalIgnoreCase)
                .FirstOrDefault()?.Value;

            var port = Get("port");
            var normalizedCom = NutComPortName.TryNormalize(port, out var com) ? com : null;

            return new NutConfiguredDriver(
                section.Name,
                Get("desc"),
                Get("driver"),
                port,
                normalizedCom,
                Get("protocol"),
                driverPath,
                new NutDriverExecutableInfo(null, NutDriverExecutableState.NotApplicable, false),
                normalizedCom is null || present is null || present.Contains(normalizedCom),
                NutDriverRuntimeState.Unknown);
        }).ToArray();
    }
}
