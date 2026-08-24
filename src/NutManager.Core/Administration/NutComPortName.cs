namespace NutManager.Core.Administration;

/// <summary>
/// The one rule for what counts as a COM port name, and what its canonical form is.
///
/// It lives on the neutral side because both sides of the product now need it. Windows enumeration
/// normalizes the names it reads out of SERIALCOMM and WMI, and the remote view has to decide whether
/// a <c>port</c> written in another machine's <c>ups.conf</c> names the same device the agent
/// reported — and those two answers have to be the same answer. A second implementation would agree
/// today and disagree the first time one of them learned something the other did not.
///
/// Pure text handling. It touches no device, no registry and no platform API.
/// </summary>
public static class NutComPortName
{
    private const string DeviceNamespacePrefix = @"\\.\";

    /// <summary>
    /// Canonicalizes a port name written in any of the forms NUT and Windows use — <c>COM4</c>,
    /// <c>\\.\COM4</c>, or either with surrounding whitespace — and refuses anything else. A value
    /// such as <c>auto</c> or a USB path is not a COM name and is rejected rather than coerced.
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var candidate = value.Trim();
        if (candidate.StartsWith(DeviceNamespacePrefix, StringComparison.Ordinal))
        {
            candidate = candidate[DeviceNamespacePrefix.Length..];
        }

        if (!candidate.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                candidate[3..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) ||
            number < 1)
        {
            return false;
        }

        normalized = "COM" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Extracts the numeric part of a normalized COM name so ordering is natural.</summary>
    public static bool TryGetNumber(string? value, out int number)
    {
        number = 0;
        return TryNormalize(value, out var normalized) &&
            int.TryParse(
                normalized[3..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out number);
    }
}
