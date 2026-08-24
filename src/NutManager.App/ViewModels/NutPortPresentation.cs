using NutManager.App.Localization;
using NutManager.Core.Administration;

namespace NutManager.App.ViewModels;

/// <summary>
/// Presentation-only formatting for serial port values. Configuration keeps whatever NUT stores;
/// this only affects how the value is displayed, so <c>ups.conf</c> and the semantic model are
/// never rewritten for cosmetic reasons.
/// </summary>
public static class NutPortPresentation
{
    private const string DevicePrefix = @"\\.\";

    /// <summary>
    /// Strips the Windows device-namespace prefix from a COM port so the UI reads <c>COM4</c>
    /// instead of <c>\\.\COM4</c>. Any value that is not a recognised COM device path is returned
    /// unchanged, so USB, HID and other transports keep their exact text.
    /// </summary>
    public static string Friendly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith(DevicePrefix, StringComparison.Ordinal)) return trimmed;

        var remainder = trimmed[DevicePrefix.Length..];
        return IsComPort(remainder) ? remainder : trimmed;
    }


    /// <summary>
    /// Drops the trailing <c>(COM3)</c> that Windows appends to a serial device's display name.
    ///
    /// The row already states the port, so repeating it inside the description is noise. Only this
    /// port's own suffix is removed, and only at the end: a parenthetical that means something else
    /// survives untouched, and a name that is nothing but the suffix is left alone rather than
    /// reduced to an empty label.
    /// </summary>
    public static string? WithoutPortSuffix(string? friendlyName, string? portName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName) || string.IsNullOrWhiteSpace(portName))
        {
            return friendlyName;
        }

        var trimmed = friendlyName.Trim();
        var suffix = "(" + portName.Trim() + ")";
        if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var remainder = trimmed[..^suffix.Length].TrimEnd();
        return remainder.Length == 0 ? trimmed : remainder;
    }

    private static bool IsComPort(string candidate) =>
        candidate.Length > 3 &&
        candidate.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        candidate[3..].All(char.IsAsciiDigit);
}

/// <summary>
/// How a detected serial port reads at a glance.
///
/// Four states, and the boundaries between them are the substance rather than the palette. Green is
/// only claimed when Windows explicitly reported no fault; grey means the port is there and nothing
/// further is known, which is the ordinary outcome for a port SERIALCOMM lists and WMI has no entry
/// for. Grey is not an error and must never be presented as one — an absent WMI record says nothing
/// about the device.
/// </summary>
public enum NutComPortHealth
{
    /// <summary>Enumerated and present; no status or fault code accompanies it.</summary>
    Unknown,

    /// <summary>Present, and Windows reported a fault code of zero.</summary>
    Healthy,

    /// <summary>Present, and Windows reported a fault code or a status other than OK.</summary>
    Warning,

    /// <summary>The port is named but the operating system does not currently expose it.</summary>
    Critical
}

/// <summary>
/// One detected serial port, ready to render.
///
/// The identity line is composed here rather than in the view, so what may and may not be claimed
/// about a device is decided in one testable place instead of in a binding nobody can assert on.
/// </summary>
public sealed record DetectedComPortViewModel(
    string PortName,
    string? FriendlyName,
    string? Manufacturer,
    string IdentityText,
    NutComPortHealth Health,
    string StatusText)
{
    public bool HasFriendlyName => !string.IsNullOrWhiteSpace(FriendlyName);

    public bool HasManufacturer => !string.IsNullOrWhiteSpace(Manufacturer);

    /// <summary>False when nothing could be established, so the second line is hidden entirely.</summary>
    public bool HasIdentity => !string.IsNullOrEmpty(IdentityText);

    public bool IsHealthy => Health == NutComPortHealth.Healthy;
    public bool IsWarning => Health == NutComPortHealth.Warning;
    public bool IsCritical => Health == NutComPortHealth.Critical;
    public bool IsUnknown => Health == NutComPortHealth.Unknown;
}

/// <summary>
/// Turns what Windows reported about a serial device into what the screen shows.
///
/// Pure and localizer-driven, which is what makes the rules assertable: every claim it makes traces
/// back to a field the operating system populated or to the fixed identifier catalogue, and a field
/// that was not populated produces no text at all. It never guesses a cable brand, a commercial
/// model, a manufacturer the device did not report, or a chipset the identifier does not establish.
/// </summary>
public static class DetectedComPortPresentation
{
    private const string Separator = " · ";

    public static DetectedComPortViewModel Create(NutComPortInfo port, NutManagerLocalizer strings)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(strings);

        var health = ResolveHealth(port);
        return new DetectedComPortViewModel(
            port.PortName,
            // The row states the port already, so the description does not repeat it.
            NutPortPresentation.WithoutPortSuffix(port.FriendlyName, port.PortName),
            port.Manufacturer,
            BuildIdentityText(port, strings),
            health,
            strings.Get(HealthKey(health)));
    }

    /// <summary>
    /// The order of the checks is the meaning. A fault is reported before health, so a device with a
    /// zero error code but a status Windows flagged is not passed off as healthy; and an entry with
    /// neither is left unknown rather than promoted to healthy, because "SERIALCOMM lists it" is
    /// evidence of presence and evidence of nothing else.
    /// </summary>
    public static NutComPortHealth ResolveHealth(NutComPortInfo port)
    {
        ArgumentNullException.ThrowIfNull(port);

        if (!port.IsPresent) return NutComPortHealth.Critical;
        if (port.ConfigManagerErrorCode is { } code && code != 0) return NutComPortHealth.Warning;
        if (!string.IsNullOrWhiteSpace(port.Status) &&
            !string.Equals(port.Status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            return NutComPortHealth.Warning;
        }

        return port.ConfigManagerErrorCode == 0 ? NutComPortHealth.Healthy : NutComPortHealth.Unknown;
    }

    /// <summary>
    /// The second line: controller, identifiers and bus, in that order, and only the parts that are
    /// actually known. An empty result means the line is not drawn.
    /// </summary>
    public static string BuildIdentityText(NutComPortInfo port, NutManagerLocalizer strings)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(strings);

        var identity = NutSerialDeviceIdentityResolver.Resolve(port);
        var parts = new List<string>(3);

        if (identity.HasChipset)
        {
            // A controller already implies its vendor, so naming both would be noise.
            parts.Add(identity.Chipset!);
        }
        else if (VendorLabel(port, identity) is { } vendor)
        {
            parts.Add(vendor);
        }

        if (identity.HasUsbIds)
        {
            parts.Add($"VID_{identity.VendorId} / PID_{identity.ProductId}");
        }

        if (BusKey(identity.Bus) is { } busKey)
        {
            parts.Add(strings.Get(busKey));
        }

        return string.Join(Separator, parts);
    }

    /// <summary>
    /// The vendor to name here, given that the manufacturer has a column of its own at the end of
    /// the row.
    ///
    /// A manufacturer the device reported is therefore never repeated on this line. The catalogue's
    /// vendor is a fallback for a device that reported none at all — and even then it is dropped when
    /// the description is already showing it, because "Prolific PL2303GT USB Serial COM Port" does
    /// not need "Prolific Technology" appended to it.
    /// </summary>
    private static string? VendorLabel(NutComPortInfo port, NutSerialDeviceIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(port.Manufacturer) || !identity.HasVendorName)
        {
            return null;
        }

        return NamesVendor(port.FriendlyName, identity.VendorName!) ? null : identity.VendorName;
    }

    private static bool NamesVendor(string? friendlyName, string vendor) =>
        friendlyName is not null && friendlyName.Contains(vendor, StringComparison.OrdinalIgnoreCase);

    private static string HealthKey(NutComPortHealth health) => health switch
    {
        NutComPortHealth.Healthy => "Administration.Drivers.PortPresentHealthy",
        NutComPortHealth.Warning => "Administration.Drivers.PortPresentWarning",
        NutComPortHealth.Critical => "Administration.Drivers.PortNotExposed",
        _ => "Administration.Drivers.PortPresentUnknown"
    };

    /// <summary>Null for an enumerator this build has no name for, so nothing is written.</summary>
    private static string? BusKey(NutSerialDeviceBus bus) => bus switch
    {
        NutSerialDeviceBus.Usb => "Administration.Drivers.Bus.Usb",
        NutSerialDeviceBus.Pci => "Administration.Drivers.Bus.Pci",
        NutSerialDeviceBus.Bluetooth => "Administration.Drivers.Bus.Bluetooth",
        NutSerialDeviceBus.Platform => "Administration.Drivers.Bus.Platform",
        _ => null
    };
}

/// <summary>
/// Where the Devices and Drivers screen is getting its device facts from.
///
/// Three states rather than two, because "there is no inspection here" and "the inspection is remote"
/// are different things an operator has to be able to tell apart. A remote profile whose agent cannot
/// be reached is <see cref="Unavailable"/> and says so; it never presents itself as a machine with no
/// serial ports.
/// </summary>
public enum NutDeviceInspectionSource
{
    /// <summary>Nothing can be inspected: no local diagnostics, or no agent answering with the capability.</summary>
    Unavailable,

    /// <summary>This machine, through the local passive enumeration.</summary>
    Local,

    /// <summary>The managed server, through the NutManager agent's read-only hardware operation.</summary>
    RemoteAgent
}
