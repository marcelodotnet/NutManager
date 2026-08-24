using System.Runtime.Versioning;
using NutManager.Core.Agent;
using NutManager.Infrastructure.Platform.Windows;

namespace NutManager.Infrastructure.Agent;

/// <summary>
/// The agent's hardware inspector, which is the existing passive COM source and nothing else.
///
/// It delegates to <see cref="IWindowsComPortSource"/> — the same enumeration the local Devices and
/// Drivers screen has always used, reading SERIALCOMM for the authoritative port names and letting
/// WMI add metadata where it happens to have some. Reusing it rather than writing a second
/// enumeration is the point: a parallel implementation is how the remote view would eventually
/// disagree with the local one, and only one of the two would be the one anybody reviewed.
///
/// What it inherits from that source is the boundary that matters. No port is opened, no byte is
/// written, no device is reconfigured, and no process is started. A NUT driver already talking to a
/// UPS on COM4 is unaffected by this running, because nothing here touches COM4.
/// </summary>
public sealed class WindowsNutAgentHardwareInspector : INutAgentHardwareInspector
{
    private readonly IWindowsComPortSource _ports;
    private readonly TimeProvider _time;
    private readonly string _machineName;

    [SupportedOSPlatform("windows")]
    public WindowsNutAgentHardwareInspector()
        : this(new WindowsWmiComPortSource())
    {
    }

    public WindowsNutAgentHardwareInspector(
        IWindowsComPortSource ports,
        TimeProvider? timeProvider = null,
        string? machineName = null)
    {
        ArgumentNullException.ThrowIfNull(ports);

        _ports = ports;
        _time = timeProvider ?? TimeProvider.System;

        // The agent's own machine, never a name a caller supplied: the request has no field for one.
        _machineName = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName;
    }

    public Task<NutAgentHardwareSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(NutAgentHardwareSnapshot.Unavailable(
                _machineName, _time.GetUtcNow(), "Serial device enumeration requires Windows."));
        }

        try
        {
            return Task.FromResult(new NutAgentHardwareSnapshot(
                _machineName, _ports.GetPorts(), true, null, _time.GetUtcNow()));
        }
        catch (Exception exception)
        {
            // "Could not be asked" rather than "has no ports". The two are different findings and an
            // operator sent to look for a missing adapter that is in fact present has been misled.
            return Task.FromResult(NutAgentHardwareSnapshot.Unavailable(
                _machineName, _time.GetUtcNow(), exception.GetType().Name));
        }
    }
}
