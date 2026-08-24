using System.Text.RegularExpressions;

namespace NutManager.Core.Administration;

/// <summary>
/// How a serial device is attached, as far as the Windows device identifier says so.
///
/// Read from the enumerator Windows itself put at the front of the PnP identifier, never guessed from
/// a friendly name. <see cref="Unknown"/> is a real answer and stays one: an identifier this build has
/// no rule for is reported as unknown rather than assigned to the most likely bus.
/// </summary>
public enum NutSerialDeviceBus
{
    Unknown,
    Usb,
    Pci,
    Bluetooth,
    Platform
}

/// <summary>
/// What can be established about a serial device from its Windows PnP identifier, and nothing more.
///
/// Every field is either read out of the identifier or looked up in a fixed local table. There is no
/// heuristic, no network lookup and no inference from marketing text, because the failure mode being
/// avoided is specific: telling an operator their adapter contains a chip it does not contain is
/// worse than telling them nothing, and it is the kind of wrong answer nobody thinks to question.
/// </summary>
/// <param name="VendorId">Four uppercase hex digits, or null when the identifier carries none.</param>
/// <param name="ProductId">Four uppercase hex digits, or null when the identifier carries none.</param>
/// <param name="Bus">How Windows enumerated the device.</param>
/// <param name="Chipset">The controller, only when the exact VID/PID pair is in the local catalogue.</param>
/// <param name="VendorName">The vendor for that VID, only when the VID is in the local catalogue.</param>
public sealed record NutSerialDeviceIdentity(
    string? VendorId,
    string? ProductId,
    NutSerialDeviceBus Bus,
    string? Chipset = null,
    string? VendorName = null)
{
    public static readonly NutSerialDeviceIdentity Unknown = new(null, null, NutSerialDeviceBus.Unknown);

    /// <summary>Both halves, which is the only form worth presenting: a VID alone identifies nothing.</summary>
    public bool HasUsbIds => VendorId is not null && ProductId is not null;

    public bool HasChipset => !string.IsNullOrEmpty(Chipset);

    public bool HasVendorName => !string.IsNullOrEmpty(VendorName);

    /// <summary>Nothing was established. The caller shows no identity line rather than an empty one.</summary>
    public bool IsEmpty => !HasUsbIds && !HasChipset && !HasVendorName && Bus == NutSerialDeviceBus.Unknown;
}

/// <summary>
/// Reads vendor and product identifiers out of a Windows PnP device identifier.
///
/// Deterministic, offline and total: every input produces an answer, and an input it cannot read
/// produces <see cref="NutSerialDeviceIdentity.Unknown"/> rather than an exception or a guess. It is
/// pure text handling, which is why it can be asserted without a device, a registry or a machine.
///
/// The shapes it accepts are the ones Windows actually produces: the USB stack writes
/// <c>USB\VID_xxxx&amp;PID_xxxx\serial</c> and FTDI's own bus enumerator writes
/// <c>FTDIBUS\VID_xxxx+PID_xxxx+serial</c>, in either case. A PCI serial port is recognised as PCI
/// but its <c>VEN_</c>/<c>DEV_</c> pair is deliberately not reported as a VID/PID: they are a
/// different identifier space, and presenting one as the other would be a small, confident lie.
/// </summary>
public static partial class NutPnpDeviceIdParser
{
    /// <summary>
    /// The identifier is bounded because it arrives over the wire. A value longer than any real
    /// device identifier is refused outright rather than handed to a regular expression.
    /// </summary>
    private const int MaxIdentifierLength = 512;

    public static NutSerialDeviceIdentity Parse(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId) || pnpDeviceId.Length > MaxIdentifierLength)
        {
            return NutSerialDeviceIdentity.Unknown;
        }

        var value = pnpDeviceId.Trim();
        var bus = ReadBus(value);
        var vendor = ReadGroup(VendorPattern(), value);
        var product = ReadGroup(ProductPattern(), value);

        // One half on its own identifies no device, so it is dropped rather than displayed as a
        // partial fact an operator might try to look up.
        return vendor is null || product is null
            ? new NutSerialDeviceIdentity(null, null, bus)
            : new NutSerialDeviceIdentity(vendor, product, bus);
    }

    /// <summary>The enumerator Windows placed before the first separator. Absent means unknown.</summary>
    private static NutSerialDeviceBus ReadBus(string value)
    {
        var separator = value.IndexOf('\\', StringComparison.Ordinal);
        var enumerator = separator > 0 ? value[..separator] : value;

        return enumerator.ToUpperInvariant() switch
        {
            "USB" => NutSerialDeviceBus.Usb,
            // FTDI's bus enumerator only ever sits on top of USB, so this is a fact about the
            // identifier rather than a guess about the hardware.
            "FTDIBUS" => NutSerialDeviceBus.Usb,
            "PCI" => NutSerialDeviceBus.Pci,
            "BTHENUM" or "BTHLE" or "BTHLEDEVICE" => NutSerialDeviceBus.Bluetooth,
            "ACPI" => NutSerialDeviceBus.Platform,
            _ => NutSerialDeviceBus.Unknown
        };
    }

    private static string? ReadGroup(Regex pattern, string value)
    {
        var match = pattern.Match(value);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    // Exactly four hex digits, not preceded or followed by another identifier character, so a longer
    // field that merely contains the letters is not mistaken for the identifier itself.
    [GeneratedRegex(@"(?<![0-9A-Za-z_])VID_([0-9A-Fa-f]{4})(?![0-9A-Fa-f])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorPattern();

    [GeneratedRegex(@"(?<![0-9A-Za-z_])PID_([0-9A-Fa-f]{4})(?![0-9A-Fa-f])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProductPattern();
}

/// <summary>
/// Names the controller behind a USB serial adapter, when — and only when — the exact identifier pair
/// is one this build knows.
///
/// The catalogue is small on purpose. Every entry is a USB-IF vendor assignment or a controller
/// identifier that is documented and stable, and an unrecognised pair resolves to no chipset at all
/// rather than to the nearest neighbour. That restraint is the requirement: a family name is reported
/// where only the family is established, and a specific variant is never reported on the strength of
/// a guess.
///
/// It performs no lookup of any kind — no internet, no file, no registry. Adding a device means
/// adding a verified row here.
/// </summary>
public static class NutSerialDeviceIdentityResolver
{
    /// <summary>
    /// Controllers, by exact vendor and product identifier.
    ///
    /// Prolific's PL2303 is entered at family level for both of its identifiers. The G-series suffixes
    /// are distinguished by product identifier in Prolific's own driver, but this build has no verified
    /// mapping for them, so it reports the family and stops there. An operator who needs the exact
    /// variant still has it in front of them: the driver's own friendly name is shown in full on the
    /// line above.
    /// </summary>
    private static readonly Dictionary<string, string> Chipsets = new(StringComparer.Ordinal)
    {
        ["067B:2303"] = "PL2303",
        ["067B:23A3"] = "PL2303",
        ["0403:6001"] = "FT232R",
        ["0403:6015"] = "FT231X",
        ["10C4:EA60"] = "CP210x",
        ["1A86:7523"] = "CH340",
        ["1A86:5523"] = "CH341"
    };

    /// <summary>Vendors, by identifier. Used only when the device itself reported no manufacturer.</summary>
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.Ordinal)
    {
        ["067B"] = "Prolific Technology",
        ["0403"] = "FTDI",
        ["10C4"] = "Silicon Labs",
        ["1A86"] = "QinHeng Electronics"
    };

    public static NutSerialDeviceIdentity Resolve(NutComPortInfo? port) => Resolve(port?.PnpDeviceId);

    public static NutSerialDeviceIdentity Resolve(string? pnpDeviceId)
    {
        var parsed = NutPnpDeviceIdParser.Parse(pnpDeviceId);
        if (!parsed.HasUsbIds) return parsed;

        var key = parsed.VendorId + ":" + parsed.ProductId;
        return parsed with
        {
            Chipset = Chipsets.TryGetValue(key, out var chipset) ? chipset : null,
            VendorName = Vendors.TryGetValue(parsed.VendorId!, out var vendor) ? vendor : null
        };
    }
}
