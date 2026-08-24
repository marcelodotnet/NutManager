using NutManager.App.Localization;
using NutManager.App.ViewModels;
using NutManager.Core.Administration;
using NutManager.Core.Models;
using NutManager.Infrastructure.Platform.Windows;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// Passive COM enumeration and its presentation. Nothing here touches the registry, WMI or a real
/// port: the normalizer and ordering are pure, and port sets are supplied directly.
/// </summary>
public sealed class WindowsComPortEnumerationTests
{
    [Theory]
    [InlineData(@"COM4", "COM4")]
    [InlineData(@"\\.\COM4", "COM4")]
    [InlineData(@"  COM12  ", "COM12")]
    [InlineData(@"\\.\COM123", "COM123")]
    public void PortNamesFromEitherSourceNormalizeToTheSameValue(string raw, string expected)
    {
        Assert.True(WindowsComPortNormalizer.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("LPT1")]
    [InlineData("COM")]
    [InlineData("COM0")]
    [InlineData("COMx")]
    [InlineData(null)]
    [InlineData("")]
    public void NonComValuesAreRejected(string? raw) =>
        Assert.False(WindowsComPortNormalizer.TryNormalize(raw, out _));

    [Fact]
    public void PortNumbersAreExtractedForNaturalOrdering()
    {
        Assert.True(WindowsComPortNormalizer.TryGetNumber(@"\\.\COM4", out var four));
        Assert.True(WindowsComPortNormalizer.TryGetNumber("COM10", out var ten));

        Assert.Equal(4, four);
        Assert.Equal(10, ten);
        // Natural order, not text order: COM4 must precede COM10.
        Assert.True(four < ten);
    }

    [Fact]
    public void DetectedPortsAreOrderedNaturally()
    {
        var source = new FakeComPortSource("COM10", "COM4", "COM2");

        var ordered = source.GetPorts().Select(port => port.PortName).ToArray();

        Assert.Equal(["COM2", "COM4", "COM10"], ordered);
    }

    [Theory]
    // The configured value keeps its raw form; only the presentation is friendly.
    [InlineData(@"\\.\COM4", "COM4")]
    [InlineData("COM4", "COM4")]
    public void ConfiguredPortIsPresentedWithoutTheDeviceNamespace(string configured, string presented) =>
        Assert.Equal(presented, NutPortPresentation.Friendly(configured));

    [Fact]
    public void ConfiguredPortIsReportedAsDetectedWhenEnumerationContainsIt()
    {
        var detected = new FakeComPortSource("COM4").GetPorts();

        var present = detected.Any(port =>
            WindowsComPortNormalizer.TryNormalize(@"\\.\COM4", out var normalized) &&
            string.Equals(port.PortName, normalized, StringComparison.OrdinalIgnoreCase));

        Assert.True(present);
    }

    [Fact]
    public void ConfiguredPortIsReportedAsNotDetectedWhenEnumerationIsEmpty()
    {
        var detected = new FakeComPortSource().GetPorts();

        var present = detected.Any(port =>
            WindowsComPortNormalizer.TryNormalize(@"\\.\COM4", out var normalized) &&
            string.Equals(port.PortName, normalized, StringComparison.OrdinalIgnoreCase));

        Assert.False(present);
        Assert.Equal("COM4", NutPortPresentation.Friendly(@"\\.\COM4"));
    }


    // ------------------------------------------------- merge: who may say a port exists

    /// <summary>
    /// The GANDALF case. `Win32_SerialPort` returns nothing at all for this USB-to-serial adapter,
    /// which is why the port used to be listed bare: no name, no manufacturer, no identifier, and a
    /// grey dot on a port that is working perfectly. `Win32_PnPEntity` knows all of it.
    /// </summary>
    [Fact]
    public void APortTheSerialClassDoesNotKnowIsEnrichedFromThePnpEntity()
    {
        var merged = WindowsComPortEnumeration.Merge(
            ["COM3"],
            [],
            [Prolific()]);

        var port = Assert.Single(merged);
        Assert.Equal("COM3", port.PortName);
        Assert.Equal("Prolific PL2303GT USB Serial COM Port (COM3)", port.FriendlyName);
        Assert.Equal("Prolific", port.Manufacturer);
        Assert.Equal(@"USB\VID_067B&PID_23A3\7&2A1B3C4D&0&2", port.PnpDeviceId);
        Assert.Equal("OK", port.Status);
        Assert.Equal(0, port.ConfigManagerErrorCode);
        Assert.True(port.IsPresent);

        // CM_PROB_NONE is a real answer, so the port reads healthy rather than unknown.
        Assert.Equal(NutComPortHealth.Healthy, DetectedComPortPresentation.ResolveHealth(port));
    }

    /// <summary>
    /// The COM2 case. Disabling a port in Device Manager removes it from SERIALCOMM and leaves the
    /// PnP entity behind, so a merge that let WMI seed the list would offer an operator a port they
    /// cannot use.
    /// </summary>
    [Fact]
    public void APnpEntityWithoutASerialCommEntryIsNotListedAtAll()
    {
        var merged = WindowsComPortEnumeration.Merge(
            ["COM3"],
            [],
            [Prolific(), Disabled()]);

        Assert.Equal(["COM3"], merged.Select(port => port.PortName));
        Assert.DoesNotContain(merged, port => port.PortName == "COM2");
    }

    [Fact]
    public void NeitherWmiClassMayIntroduceAPort()
    {
        // Not only the fallback: the more specific class is enrichment too, and SERIALCOMM alone
        // decides existence.
        var merged = WindowsComPortEnumeration.Merge(
            [],
            [new WindowsComPortMetadata("COM5", "Communications Port (COM5)", null, null, "OK", 0)],
            [Prolific()]);

        Assert.Empty(merged);
    }

    [Fact]
    public void AnEmptyDeviceMapYieldsNoPortsHoweverMuchWmiKnows()
    {
        Assert.Empty(WindowsComPortEnumeration.Merge([], [], [Prolific(), Disabled()]));
    }

    // ------------------------------------------------- merge: priority and gap filling

    [Fact]
    public void TheSerialClassWinsAndThePnpEntityOnlyFillsWhatIsStillMissing()
    {
        var primary = new WindowsComPortMetadata("COM3", "Serial class name", null, null, "OK", 0);
        var fallback = new WindowsComPortMetadata(
            null, "Prolific PL2303GT USB Serial COM Port (COM3)", "Prolific", @"USB\VID_067B&PID_23A3\1", "Degraded", 10);

        var port = Assert.Single(WindowsComPortEnumeration.Merge(["COM3"], [primary], [fallback]));

        // Kept from the more specific class.
        Assert.Equal("Serial class name", port.FriendlyName);
        Assert.Equal("OK", port.Status);
        Assert.Equal(0, port.ConfigManagerErrorCode);
        // Filled from the fallback, because the primary had nothing to say.
        Assert.Equal("Prolific", port.Manufacturer);
        Assert.Equal(@"USB\VID_067B&PID_23A3\1", port.PnpDeviceId);
    }

    [Fact]
    public void AFaultCodeOfZeroIsAValueAndIsNotOverwrittenByTheFallback()
    {
        // The bug this guards: treating CM_PROB_NONE as "nothing reported" and letting a later,
        // less specific row replace a clean port with a faulty one.
        var primary = new WindowsComPortMetadata("COM3", null, null, null, null, 0);
        var fallback = new WindowsComPortMetadata(null, "Thing (COM3)", null, null, null, 43);

        var port = Assert.Single(WindowsComPortEnumeration.Merge(["COM3"], [primary], [fallback]));

        Assert.Equal(0, port.ConfigManagerErrorCode);
        Assert.Equal(NutComPortHealth.Healthy, DetectedComPortPresentation.ResolveHealth(port));
    }

    [Fact]
    public void AnEmptyWmiValueDoesNotBlockTheFallback()
    {
        var primary = new WindowsComPortMetadata("COM3", "   ", string.Empty, null, null, null);

        var port = Assert.Single(WindowsComPortEnumeration.Merge(["COM3"], [primary], [Prolific()]));

        Assert.Equal("Prolific PL2303GT USB Serial COM Port (COM3)", port.FriendlyName);
        Assert.Equal("Prolific", port.Manufacturer);
    }

    [Fact]
    public void APortWithNoMetadataAtAllStaysPresentAndUnknownRatherThanFaulty()
    {
        var port = Assert.Single(WindowsComPortEnumeration.Merge(["COM3"], [], []));

        Assert.True(port.IsPresent);
        Assert.Null(port.Status);
        Assert.Null(port.ConfigManagerErrorCode);
        // Absent WMI is not an error, and it never was.
        Assert.Equal(NutComPortHealth.Unknown, DetectedComPortPresentation.ResolveHealth(port));
    }

    [Fact]
    public void MergedPortsKeepNaturalOrdering()
    {
        var merged = WindowsComPortEnumeration.Merge([@"\\.\COM10", "COM4", "COM2"], [], []);

        Assert.Equal(["COM2", "COM4", "COM10"], merged.Select(port => port.PortName));
    }

    // ------------------------------------------------- associating a PnP entity with its port

    [Theory]
    [InlineData("Prolific PL2303GT USB Serial COM Port (COM3)", "COM3")]
    [InlineData("Communications Port (COM1)", "COM1")]
    [InlineData("USB Serial Device (COM12)", "COM12")]
    [InlineData("prolific usb-to-serial comm port (com3)", "COM3")]
    // A vendor is free to write anything before the suffix Windows appends, so the last group wins.
    [InlineData("Adapter (COM9) rev B (COM3)", "COM3")]
    public void ThePortIsReadFromTheDisplayNameSuffix(string displayName, string expected)
    {
        Assert.True(WindowsComPortEnumeration.TryReadPortFromDisplayName(displayName, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Prolific USB-to-Serial Comm Port")]
    [InlineData("Thing (COM)")]
    [InlineData("Thing (COMx)")]
    [InlineData("Thing (COM0)")]
    [InlineData("Thing COM3")]
    [InlineData("Intel(R) Active Management Technology")]
    public void ANameWithoutAPortSuffixResolvesToNothing(string? displayName) =>
        Assert.False(WindowsComPortEnumeration.TryReadPortFromDisplayName(displayName, out _));

    [Fact]
    public void AnEntityThatNamesNoPortEnrichesNothing()
    {
        // Wrongly attaching one device's identity to another device's port would be worse than
        // showing no identity at all, so an unresolvable row is dropped.
        var anonymous = new WindowsComPortMetadata(null, "Some device with no port suffix", "Vendor", "PNP", "OK", 0);

        var port = Assert.Single(WindowsComPortEnumeration.Merge(["COM3"], [], [anonymous]));

        Assert.Null(port.FriendlyName);
        Assert.Null(port.Manufacturer);
    }

    [Fact]
    public void TheDeviceIdIsPreferredOverTheDisplayNameWhenBothArePresent()
    {
        var conflicting = new WindowsComPortMetadata("COM3", "Mislabelled (COM9)", "Vendor", null, "OK", 0);

        var port = Assert.Single(
            WindowsComPortEnumeration.Merge(["COM3", "COM9"], [conflicting], []),
            entry => entry.Manufacturer is not null);

        Assert.Equal("COM3", port.PortName);
    }

    // ------------------------------------------------- the observed device, end to end

    [Fact]
    public void TheObservedAdapterIsPresentedExactlyAsExpected()
    {
        var port = Assert.Single(WindowsComPortEnumeration.Merge(["COM3"], [], [Prolific()]));

        var presented = DetectedComPortPresentation.Create(port, new NutManagerLocalizer(UiLanguagePreference.PtBr));

        Assert.True(presented.IsHealthy);
        Assert.Equal("COM3", presented.PortName);
        // The row states the port, so the description no longer repeats it as "(COM3)".
        Assert.Equal("Prolific PL2303GT USB Serial COM Port", presented.FriendlyName);
        // The catalogue stays at family level; the variant is already in the description above, and
        // "Prolific" is not appended because that description is already showing it.
        Assert.Equal("PL2303 · VID_067B / PID_23A3 · USB–Serial", presented.IdentityText);
        Assert.DoesNotContain("(COM3)", presented.FriendlyName!, StringComparison.Ordinal);
    }



    // ------------------------------------------------- the description does not repeat the port

    [Theory]
    [InlineData("Prolific PL2303GT USB Serial COM Port (COM3)", "COM3", "Prolific PL2303GT USB Serial COM Port")]
    [InlineData("Communications Port (COM1)", "COM1", "Communications Port")]
    [InlineData("USB Serial Device (com12)", "COM12", "USB Serial Device")]
    // Only this port's own suffix, and only at the end.
    [InlineData("Adapter (COM9)", "COM3", "Adapter (COM9)")]
    [InlineData("Adapter (COM3) rev B", "COM3", "Adapter (COM3) rev B")]
    // Nothing to strip.
    [InlineData("Prolific USB-to-Serial Comm Port", "COM3", "Prolific USB-to-Serial Comm Port")]
    // A name that is only the suffix keeps it rather than becoming an empty label.
    [InlineData("(COM3)", "COM3", "(COM3)")]
    public void ThePortSuffixIsDroppedFromTheDescription(string friendlyName, string portName, string expected) =>
        Assert.Equal(expected, NutPortPresentation.WithoutPortSuffix(friendlyName, portName));

    [Theory]
    [InlineData(null, "COM3")]
    [InlineData("", "COM3")]
    [InlineData("Adapter (COM3)", null)]
    [InlineData("Adapter (COM3)", "")]
    public void AMissingNameOrPortLeavesTheDescriptionUntouched(string? friendlyName, string? portName) =>
        Assert.Equal(friendlyName, NutPortPresentation.WithoutPortSuffix(friendlyName, portName));

    // ------------------------------------------------- the enumeration stays passive

    [Fact]
    public void TheEnumerationOpensNothingAndRunsNothing()
    {
        // The whole enumeration block, read as source: the COM source, the metadata row and the
        // merge, which sit contiguously between these two classes. Bounding the scan this way keeps
        // the driver diagnostics runner — which legitimately starts NUT tools — out of it, and fails
        // loudly if the block is ever moved rather than passing silently.
        var file = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "NutManager.Infrastructure", "Platform", "Windows", "WindowsNutDriverDiagnostics.cs"));
        var start = file.IndexOf("public sealed class WindowsWmiComPortSource", StringComparison.Ordinal);
        var end = file.IndexOf("public sealed class WindowsNutDriverDiagnostics", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start, "The COM enumeration block was not found where the scan expects it.");
        var region = file[start..end];

        string[] forbidden =
        [
            "SerialPort.Open", "SerialPort(", "CreateFile", "WriteFile", "ReadFile",
            "Process.Start", "ProcessStartInfo", "cmd.exe", "powershell", "Get-PnpDevice",
            "SetValue", "DeleteValue", "DeleteSubKey", "File.WriteAllText", "File.Delete"
        ];

        foreach (var token in forbidden)
        {
            Assert.DoesNotContain(token, region, StringComparison.OrdinalIgnoreCase);
        }

        // Reads only, and only the two classes and the one key the enumeration is defined in terms of.
        Assert.Contains("OpenSubKey", region, StringComparison.Ordinal);
        Assert.Contains("Win32_SerialPort", region, StringComparison.Ordinal);
        Assert.Contains("Win32_PnPEntity", region, StringComparison.Ordinal);
    }

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

    private static WindowsComPortMetadata Prolific() => new(
        null,
        "Prolific PL2303GT USB Serial COM Port (COM3)",
        "Prolific",
        @"USB\VID_067B&PID_23A3\7&2A1B3C4D&0&2",
        "OK",
        0);

    /// <summary>A port Device Manager still knows and SERIALCOMM no longer lists.</summary>
    private static WindowsComPortMetadata Disabled() => new(
        null, "Communications Port (COM2)", "(Standard port types)", @"ACPI\PNP0501\1", "OK", 0);

    /// <summary>Applies the same normalization and natural ordering as the real source.</summary>
    private sealed class FakeComPortSource(params string[] names) : IWindowsComPortSource
    {
        public IReadOnlyList<NutComPortInfo> GetPorts() => names
            .Select(name => WindowsComPortNormalizer.TryNormalize(name, out var normalized) ? normalized : null)
            .Where(name => name is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new NutComPortInfo(name!, null, null, null, null, null, true))
            .OrderBy(port => WindowsComPortNormalizer.TryGetNumber(port.PortName, out var number) ? number : int.MaxValue)
            .ToArray();
    }
}
