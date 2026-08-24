using System.Text;
using System.Text.Json;
using NutManager.App.Localization;
using NutManager.App.Services;
using NutManager.App.ViewModels;
using NutManager.Core.Administration;
using NutManager.Core.Agent;
using NutManager.Core.Configuration;
using NutManager.Core.Models;
using NutManager.Core.Services;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// T38 — remote COM and hardware inspection through the agent.
///
/// Everything here runs without a device, a registry, a pipe, an HTTPS listener or a NUT server. The
/// parser and the identifier catalogue are pure; the agent is exercised through its own application
/// service with a fake inspector; and the screen is exercised through the view model with a fake
/// client. What is asserted is not that the code runs but that it cannot claim more than it knows:
/// no invented chipset, no invented manufacturer, no port opened, and above all no unreachable agent
/// presented as a server with no serial ports.
/// </summary>
public sealed class T38RemoteDeviceInspectionTests
{
    // ---------------------------------------------------------------- PnP identifier parsing

    [Theory]
    [InlineData(@"USB\VID_067B&PID_23A3\5&1D2C1B3&0&2", "067B", "23A3")]
    [InlineData(@"usb\vid_067b&pid_23a3\5&1d2c1b3&0&2", "067B", "23A3")]
    [InlineData(@"USB\VID_0403&PID_6001\A9012ABC", "0403", "6001")]
    // FTDI's own bus enumerator separates with '+' rather than '&'.
    [InlineData(@"FTDIBUS\VID_0403+PID_6001+A9012ABCA\0000", "0403", "6001")]
    public void VendorAndProductAreReadFromTheIdentifierInEitherCase(string pnpDeviceId, string vendor, string product)
    {
        var identity = NutPnpDeviceIdParser.Parse(pnpDeviceId);

        Assert.Equal(vendor, identity.VendorId);
        Assert.Equal(product, identity.ProductId);
        Assert.True(identity.HasUsbIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("USB")]
    [InlineData(@"USB\")]
    // A serial-only identifier with no vendor or product fields at all.
    [InlineData(@"ACPI\PNP0501\1")]
    // Half an identifier is not an identifier: one field alone names no device.
    [InlineData(@"USB\VID_067B\5&1D2C1B3")]
    [InlineData(@"USB\PID_23A3\5&1D2C1B3")]
    public void AnIdentifierWithoutBothFieldsYieldsNoIdentifiers(string? pnpDeviceId)
    {
        var identity = NutPnpDeviceIdParser.Parse(pnpDeviceId);

        Assert.Null(identity.VendorId);
        Assert.Null(identity.ProductId);
        Assert.False(identity.HasUsbIds);
    }

    [Theory]
    [InlineData(@"USB\VID_067B&PID_23A3\X", NutSerialDeviceBus.Usb)]
    [InlineData(@"FTDIBUS\VID_0403+PID_6001+A\0000", NutSerialDeviceBus.Usb)]
    [InlineData(@"PCI\VEN_1415&DEV_C158\4&1", NutSerialDeviceBus.Pci)]
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\7", NutSerialDeviceBus.Bluetooth)]
    [InlineData(@"ACPI\PNP0501\1", NutSerialDeviceBus.Platform)]
    [InlineData(@"SOMETHINGELSE\ABC\1", NutSerialDeviceBus.Unknown)]
    public void TheBusComesFromTheEnumeratorWindowsWrote(string pnpDeviceId, NutSerialDeviceBus expected) =>
        Assert.Equal(expected, NutPnpDeviceIdParser.Parse(pnpDeviceId).Bus);

    [Fact]
    public void APciSerialPortIsNotGivenAUsbIdentifierItDoesNotHave()
    {
        // VEN_/DEV_ is a different identifier space. Reporting it as VID_/PID_ would be a small,
        // confident lie an operator could not tell from the truth.
        var identity = NutPnpDeviceIdParser.Parse(@"PCI\VEN_1415&DEV_C158\4&1");

        Assert.Equal(NutSerialDeviceBus.Pci, identity.Bus);
        Assert.Null(identity.VendorId);
        Assert.Null(identity.ProductId);
    }

    // ---------------------------------------------------------------- identifier catalogue

    [Fact]
    public void AKnownPairResolvesToItsControllerAndVendor()
    {
        var identity = NutSerialDeviceIdentityResolver.Resolve(@"USB\VID_067B&PID_23A3\5&1D2C1B3&0&2");

        Assert.Equal("PL2303", identity.Chipset);
        Assert.Equal("Prolific Technology", identity.VendorName);
        Assert.Equal(NutSerialDeviceBus.Usb, identity.Bus);
    }

    [Fact]
    public void AProlificVariantIsReportedAtFamilyLevelAndNeverAsAGuessedSuffix()
    {
        // The G-series suffix is not established by any offline source this build owns, so the family
        // is reported and the suffix is not invented. The driver's own friendly name still carries it.
        foreach (var id in new[] { @"USB\VID_067B&PID_2303\6&1", @"USB\VID_067B&PID_23A3\6&1" })
        {
            var identity = NutSerialDeviceIdentityResolver.Resolve(id);

            Assert.Equal("PL2303", identity.Chipset);
            Assert.DoesNotContain("GT", identity.Chipset!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnUnknownPairKeepsItsIdentifiersAndInventsNothingElse()
    {
        var identity = NutSerialDeviceIdentityResolver.Resolve(@"USB\VID_ABCD&PID_1234\9&9");

        Assert.Equal("ABCD", identity.VendorId);
        Assert.Equal("1234", identity.ProductId);
        Assert.Null(identity.Chipset);
        Assert.Null(identity.VendorName);
        Assert.False(identity.HasChipset);
        Assert.False(identity.HasVendorName);
    }

    [Fact]
    public void AnIdentifierWithNothingUsableInItIsEmpty()
    {
        var identity = NutSerialDeviceIdentityResolver.Resolve("not a device identifier");

        Assert.True(identity.IsEmpty);
        Assert.Same(NutSerialDeviceIdentity.Unknown, NutPnpDeviceIdParser.Parse(null));
    }

    // ---------------------------------------------------------------- port presentation

    [Theory]
    // Windows explicitly said there is no problem.
    [InlineData(true, 0, "OK", NutComPortHealth.Healthy)]
    // A fault code, or a status that is not OK, is attention — never silently healthy.
    [InlineData(true, 10, "Error", NutComPortHealth.Warning)]
    [InlineData(true, 0, "Degraded", NutComPortHealth.Warning)]
    // SERIALCOMM listed it and WMI added nothing. Present, and nothing further known.
    [InlineData(true, null, null, NutComPortHealth.Unknown)]
    // Named but not exposed by the operating system right now.
    [InlineData(false, null, null, NutComPortHealth.Critical)]
    public void PortHealthFollowsWhatWindowsActuallyReported(
        bool present, int? errorCode, string? status, NutComPortHealth expected) =>
        Assert.Equal(expected, DetectedComPortPresentation.ResolveHealth(
            new NutComPortInfo("COM4", null, null, null, status, errorCode, present)));

    [Fact]
    public void AbsentWmiMetadataIsNeverTurnedIntoAFault()
    {
        // The regression this exists to prevent: SERIALCOMM is authoritative for presence and WMI is
        // enrichment, so a port WMI has no record of is still a present port.
        var port = new NutComPortInfo("COM4", null, null, null, null, null, true);

        var presented = DetectedComPortPresentation.Create(port, Strings());

        Assert.Equal(NutComPortHealth.Unknown, presented.Health);
        Assert.False(presented.IsCritical);
        Assert.False(presented.IsWarning);
        Assert.False(presented.HasIdentity);
        Assert.Equal("COM4", presented.PortName);
    }

    [Fact]
    public void TheIdentityLineCarriesControllerIdentifiersAndBus()
    {
        var port = new NutComPortInfo(
            "COM4", "Prolific USB-to-Serial Comm Port", "Prolific",
            @"USB\VID_067B&PID_23A3\5&1D2C1B3&0&2", "OK", 0, true);

        var presented = DetectedComPortPresentation.Create(port, Strings());

        Assert.True(presented.HasIdentity);
        Assert.Equal("PL2303 · VID_067B / PID_23A3 · USB–Serial", presented.IdentityText);
        Assert.True(presented.IsHealthy);
    }

    [Fact]
    public void TheVendorIsNamedOnlyWhereNothingElseIsAlreadyShowingIt()
    {
        // The manufacturer has a column of its own at the end of the row, so the identity line never
        // repeats one the device reported.
        var reported = new NutComPortInfo("COM4", "Adapter", "Prolific", @"USB\VID_067B&PID_9999\1", "OK", 0, true);
        // Nothing reported: the catalogue is the only thing that can name a vendor.
        var silent = new NutComPortInfo("COM5", "Adapter", null, @"USB\VID_067B&PID_9999\1", "OK", 0, true);
        // Nothing reported, but the description already carries it.
        var described = new NutComPortInfo("COM6", "Prolific Technology Adapter", null, @"USB\VID_067B&PID_9999\1", "OK", 0, true);

        Assert.Equal("VID_067B / PID_9999 · USB–Serial", DetectedComPortPresentation.BuildIdentityText(reported, Strings()));
        Assert.Equal("Prolific Technology · VID_067B / PID_9999 · USB–Serial", DetectedComPortPresentation.BuildIdentityText(silent, Strings()));
        Assert.Equal("VID_067B / PID_9999 · USB–Serial", DetectedComPortPresentation.BuildIdentityText(described, Strings()));
    }

    [Fact]
    public void AKnownControllerStandsInForItsVendor()
    {
        // PL2303 is a Prolific part; naming both would say the same thing twice.
        var port = new NutComPortInfo("COM3", "Adapter", "Prolific", @"USB\VID_067B&PID_23A3\1", "OK", 0, true);

        Assert.Equal("PL2303 · VID_067B / PID_23A3 · USB–Serial", DetectedComPortPresentation.BuildIdentityText(port, Strings()));
    }

    // ---------------------------------------------------------------- protocol

    [Fact]
    public async Task AnAgentWithAnInspectorAdvertisesTheCapabilityEvenWhenControlIsUnavailable()
    {
        // No NUT service could be pinned, so control is off. Serial devices exist regardless, and
        // refusing to describe them because control is unavailable would report a readable machine
        // as having no ports.
        var service = CreateAgent(new RecordingInspector(Port("COM4")), targetResolved: false);
        await service.InitializeAsync(default);

        var handshake = await service.HandshakeAsync(default);

        Assert.False(handshake.ControlAvailable);
        Assert.Contains(NutAgentOperation.GetHardwareSnapshot, handshake.Capabilities);
        Assert.DoesNotContain(NutAgentOperation.Start, handshake.Capabilities);
    }

    [Fact]
    public async Task AnAgentBuiltWithoutAnInspectorDoesNotAdvertiseTheCapability()
    {
        // This is what an agent predating T38 looks like to a current client: the operation is simply
        // absent from the handshake, which is how the client learns not to ask for it.
        var service = CreateAgent(inspector: null, targetResolved: true);
        await service.InitializeAsync(default);

        var handshake = await service.HandshakeAsync(default);

        Assert.True(handshake.ControlAvailable);
        Assert.DoesNotContain(NutAgentOperation.GetHardwareSnapshot, handshake.Capabilities);
        Assert.Equal(NutAgentOptions.ProtocolVersion, handshake.ProtocolVersion);
    }

    [Fact]
    public void TheProtocolVersionIsUnchangedSoAnOlderAgentStillAnswersEverythingElse()
    {
        // The capability is negotiated through the handshake, not through the version. Bumping the
        // version would make an older agent refuse every request from a current client, including the
        // handshake that would have told it what is supported.
        Assert.Equal(1, NutAgentOptions.ProtocolVersion);
    }

    [Fact]
    public void TheRequestForHardwareCarriesNothingButTheOperation()
    {
        var payload = NutAgentWireCodec.Serialize(
            NutAgentRequest.For(NutAgentOperation.GetHardwareSnapshot, Guid.NewGuid()));
        using var document = JsonDocument.Parse(payload);

        // Three fields, and none of them can name a port, a speed, a command or an executable.
        Assert.Equal(
            ["protocolVersion", "operation", "operationId"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.True(NutAgentWireCodec.TryReadRequest(payload, out var request, out var failure));
        Assert.Equal(NutAgentResultCode.Success, failure);
        Assert.Equal(NutAgentOperation.GetHardwareSnapshot, request!.Operation);
    }

    [Fact]
    public void TheHardwareResponseSurvivesARoundTripAndCarriesNoSecret()
    {
        var response = new NutAgentResponse(
            NutAgentOptions.ProtocolVersion,
            NutAgentResultCode.Success,
            Hardware: new NutAgentHardwareSnapshot(
                "GANDALF", [Port("COM4")], true, null, DateTimeOffset.UnixEpoch));

        var payload = NutAgentWireCodec.Serialize(response);

        Assert.True(NutAgentWireCodec.TryReadResponse(payload, out var parsed, out var failure));
        Assert.Equal(NutAgentResultCode.Success, failure);
        Assert.Equal("GANDALF", parsed!.Hardware!.MachineName);
        Assert.Equal("COM4", Assert.Single(parsed.Hardware.ComPorts).PortName);
        Assert.True(parsed.Hardware.EnumerationSucceeded);

        var text = Encoding.UTF8.GetString(payload);
        foreach (var forbidden in new[] { "password", "secret", "token", "credential" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnOperationThisBuildDoesNotDefineStillFailsClosed()
    {
        var payload = Encoding.UTF8.GetBytes(
            $$"""{"protocolVersion":{{NutAgentOptions.ProtocolVersion}},"operation":"OpenSerialPort","operationId":"{{Guid.NewGuid()}}"}""");

        Assert.False(NutAgentWireCodec.TryReadRequest(payload, out var request, out var failure));
        Assert.Null(request);
        Assert.Equal(NutAgentResultCode.MalformedRequest, failure);
    }

    // ---------------------------------------------------------------- agent behaviour

    [Fact]
    public async Task TheSnapshotComesFromThePassiveSourceAndFromNothingElse()
    {
        var inspector = new RecordingInspector(Port("COM4"), Port("COM7"));
        var service = CreateAgent(inspector, targetResolved: true);
        await service.InitializeAsync(default);

        var snapshot = await service.GetHardwareSnapshotAsync(default);

        Assert.Equal(1, inspector.Calls);
        Assert.True(snapshot.EnumerationSucceeded);
        Assert.Equal(["COM4", "COM7"], snapshot.ComPorts.Select(port => port.PortName));
        Assert.True(snapshot.HasPorts);
    }

    [Fact]
    public async Task AnInspectorThatThrowsIsReportedRatherThanTakingTheAgentWithIt()
    {
        var service = CreateAgent(new ThrowingInspector(), targetResolved: true);
        await service.InitializeAsync(default);

        var snapshot = await service.GetHardwareSnapshotAsync(default);

        // Not an empty machine: the difference between "no ports" and "could not ask" survives.
        Assert.False(snapshot.EnumerationSucceeded);
        Assert.Empty(snapshot.ComPorts);
        Assert.False(snapshot.HasPorts);
        Assert.NotNull(snapshot.Detail);
    }

    [Fact]
    public async Task AnAgentWithoutAnInspectorRefusesTheReadInsteadOfInventingAnEmptyMachine()
    {
        var service = CreateAgent(inspector: null, targetResolved: true);
        await service.InitializeAsync(default);

        var snapshot = await service.GetHardwareSnapshotAsync(default);

        Assert.False(snapshot.EnumerationSucceeded);
        Assert.Empty(snapshot.ComPorts);
    }

    [Fact]
    public async Task AnOversizedPortListIsCappedRatherThanBecomingAnUnreadableFrame()
    {
        var many = Enumerable.Range(1, NutAgentHardware.MaxReportedPorts + 20)
            .Select(number => Port("COM" + number))
            .ToArray();
        var service = CreateAgent(new RecordingInspector(many), targetResolved: true);
        await service.InitializeAsync(default);

        var snapshot = await service.GetHardwareSnapshotAsync(default);

        Assert.Equal(NutAgentHardware.MaxReportedPorts, snapshot.ComPorts.Count);
        Assert.True(snapshot.EnumerationSucceeded);
        Assert.NotNull(snapshot.Detail);
        // Still inside what both transports will carry.
        Assert.True(NutAgentWireCodec.Serialize(new NutAgentResponse(
            NutAgentOptions.ProtocolVersion, NutAgentResultCode.Success, Hardware: snapshot)).Length
            <= NutAgentFraming.MaxResponseBytes);
    }

    [Fact]
    public async Task BothTransportsAnswerTheSameOperationThroughTheSameDispatcher()
    {
        // The pipe server and the HTTPS listener both call this one dispatcher, so asserting it here
        // is asserting both: neither can grow its own opinion about what the operation means.
        var service = CreateAgent(new RecordingInspector(Port("COM4")), targetResolved: true);
        await service.InitializeAsync(default);
        var dispatcher = new NutAgentRequestDispatcher(service);

        foreach (var transport in new[] { NutAgentNamedPipe.TransportName, NutAgentHttpsProtocol.TransportName })
        {
            var response = await dispatcher.DispatchAsync(
                NutAgentRequest.For(NutAgentOperation.GetHardwareSnapshot, Guid.NewGuid()),
                new NutAgentCallerContext(@"EXAMPLE\operator", true, transport),
                default);

            Assert.Equal(NutAgentResultCode.Success, response.Code);
            Assert.Equal("COM4", Assert.Single(response.Hardware!.ComPorts).PortName);
            Assert.Null(response.Result);
            Assert.Null(response.Status);
        }
    }

    [Fact]
    public async Task ReadingHardwareWritesNoAuditRecord()
    {
        // Mutations are audited and stay audited. A read an operator can repeat by pressing Refresh
        // must not be able to bury a control record under enumeration noise.
        var audit = new RecordingAudit();
        var service = CreateAgent(new RecordingInspector(Port("COM4")), targetResolved: true, audit: audit);
        await service.InitializeAsync(default);
        audit.Entries.Clear();

        await service.GetHardwareSnapshotAsync(default);

        Assert.Empty(audit.Entries);
    }

    // ---------------------------------------------------------------- the screen

    [Fact]
    public async Task ALocalProfileInspectsLocallyAndNeverReachesForAnAgent()
    {
        var client = new StubAgentClient();
        var viewModel = CreateLocalViewModel(client, Port("COM4"));

        await viewModel.InitializeAsync();

        Assert.Equal(NutDeviceInspectionSource.Local, viewModel.DeviceInspectionSource);
        Assert.False(viewModel.IsRemoteDeviceInspection);
        Assert.True(viewModel.IsComPortListKnown);
        Assert.Equal("COM4", Assert.Single(viewModel.DetectedComPorts).PortName);
        Assert.Empty(client.Operations);
        Assert.True(viewModel.AreActiveDiagnosticsAvailable);
    }

    [Fact]
    public async Task ARemoteProfileWithACapableAgentInspectsThroughIt()
    {
        var client = new StubAgentClient { Ports = [Port("COM4")] };
        var viewModel = CreateRemoteViewModel(client);

        await viewModel.InitializeAsync();

        Assert.Equal(NutDeviceInspectionSource.RemoteAgent, viewModel.DeviceInspectionSource);
        Assert.True(viewModel.IsDeviceInspectionAvailable);
        Assert.True(viewModel.IsComPortListKnown);
        Assert.Equal("COM4", Assert.Single(viewModel.DetectedComPorts).PortName);
        Assert.Equal([NutAgentOperation.Handshake, NutAgentOperation.GetHardwareSnapshot], client.Operations);

        // The source is named, and it is never described as a local diagnostic.
        Assert.Equal(viewModel.Strings.Get("Administration.Drivers.SourceRemoteAgent"), viewModel.DeviceInspectionSourceText);
        // Active diagnostics stay local even when inspection is remote.
        Assert.False(viewModel.AreActiveDiagnosticsAvailable);
    }

    [Fact]
    public async Task AnAgentWithoutTheCapabilityIsReportedAsUnavailableRatherThanAsked()
    {
        var client = new StubAgentClient { Capabilities = [NutAgentOperation.Handshake, NutAgentOperation.GetStatus] };
        var viewModel = CreateRemoteViewModel(client);

        await viewModel.InitializeAsync();

        Assert.Equal(NutDeviceInspectionSource.Unavailable, viewModel.DeviceInspectionSource);
        Assert.True(viewModel.IsDeviceInspectionUnavailable);
        // The snapshot was never requested: an unsupported operation is not sent.
        Assert.Equal([NutAgentOperation.Handshake], client.Operations);
        Assert.Equal(
            viewModel.Strings.Get("Administration.Drivers.RemoteCapabilityMissing"),
            viewModel.DriverDiagnosticStatusMessage);
    }

    [Fact]
    public async Task AnUnreachableAgentIsNeverPresentedAsAServerWithNoPorts()
    {
        var client = new StubAgentClient { HandshakeStatus = NutAgentClientStatus.AgentUnavailable };
        var viewModel = CreateRemoteViewModel(client);

        await viewModel.InitializeAsync();

        Assert.Equal(NutDeviceInspectionSource.Unavailable, viewModel.DeviceInspectionSource);
        Assert.False(viewModel.IsComPortListKnown);
        // The distinction the whole task turns on: no port list is not an empty port list.
        Assert.False(viewModel.HasNoComPorts);
        Assert.Empty(viewModel.DetectedComPorts);
    }

    [Fact]
    public async Task AnAgentThatCannotEnumerateIsNotReportedAsAMachineWithoutPorts()
    {
        var client = new StubAgentClient { EnumerationSucceeded = false };
        var viewModel = CreateRemoteViewModel(client);

        await viewModel.InitializeAsync();

        Assert.Equal(NutDeviceInspectionSource.RemoteAgent, viewModel.DeviceInspectionSource);
        Assert.False(viewModel.IsComPortListKnown);
        Assert.False(viewModel.HasNoComPorts);
        Assert.Equal(
            viewModel.Strings.Get("Administration.Drivers.RemoteEnumerationFailed"),
            viewModel.DriverDiagnosticStatusMessage);
    }

    [Fact]
    public async Task TheConfiguredPortIsRelatedToWhatTheServerActuallyReported()
    {
        var client = new StubAgentClient { Ports = [Port("COM4")] };
        var pipeline = new StubPipeline("/etc/nut/ups.conf", "[NOBREAK]\ndriver = nutdrv_qx\nport = COM4\nprotocol = q1\n");
        var viewModel = CreateRemoteViewModel(client, pipeline);

        await viewModel.InitializeAsync();

        var driver = Assert.Single(viewModel.ConfiguredDrivers);
        Assert.Equal("NOBREAK", driver.UpsName);
        Assert.Equal("nutdrv_qx", driver.DriverName);
        Assert.Equal("COM4", driver.NormalizedComPort);
        Assert.True(driver.IsConfiguredComPortPresent);

        // Nothing about the remote executable or its process is claimed from here.
        Assert.Equal(NutDriverExecutableState.NotApplicable, driver.Executable.State);
        Assert.Equal(NutDriverRuntimeState.Unknown, driver.RuntimeState);

        viewModel.SelectedConfiguredDriver = driver;
        Assert.True(viewModel.HasSelectedDriverPortState);
        Assert.True(viewModel.IsSelectedDriverPortPresent);
        Assert.Equal(viewModel.Strings.Get("Administration.Drivers.PortDetectedOnServer"), viewModel.SelectedDriverPortStateText);
    }

    [Fact]
    public async Task AConfiguredPortTheServerDoesNotExposeIsReportedAsMissingOnTheServer()
    {
        var client = new StubAgentClient { Ports = [Port("COM7")] };
        var pipeline = new StubPipeline("/etc/nut/ups.conf", "[NOBREAK]\ndriver = nutdrv_qx\nport = COM4\n");
        var viewModel = CreateRemoteViewModel(client, pipeline);

        await viewModel.InitializeAsync();

        var driver = Assert.Single(viewModel.ConfiguredDrivers);
        Assert.False(driver.IsConfiguredComPortPresent);

        viewModel.SelectedConfiguredDriver = driver;
        Assert.True(viewModel.HasSelectedDriverPortState);
        Assert.Equal(viewModel.Strings.Get("Administration.Drivers.PortNotDetectedOnServer"), viewModel.SelectedDriverPortStateText);
    }

    [Fact]
    public void AnUnknownPortListNeverMakesAConfiguredPortReadAsAbsent()
    {
        // The reader is given null rather than an empty list, which is what "nobody could look" means.
        var document = new NutConfigurationParser().Parse(
            NutConfigurationFileKind.UpsConf, "[NOBREAK]\ndriver = nutdrv_qx\nport = COM4\n");

        var withoutPorts = Assert.Single(NutRemoteConfiguredDriverReader.Read(document, null));
        var withEmptyList = Assert.Single(NutRemoteConfiguredDriverReader.Read(document, []));

        Assert.True(withoutPorts.IsConfiguredComPortPresent);
        Assert.False(withEmptyList.IsConfiguredComPortPresent);
    }

    [Fact]
    public void ANonComPortValueIsNotTreatedAsAContradiction()
    {
        var document = new NutConfigurationParser().Parse(
            NutConfigurationFileKind.UpsConf, "[USBUPS]\ndriver = usbhid-ups\nport = auto\n");

        var driver = Assert.Single(NutRemoteConfiguredDriverReader.Read(document, [Port("COM4")]));

        Assert.Null(driver.NormalizedComPort);
        Assert.True(driver.IsConfiguredComPortPresent);
    }

    [Fact]
    public void TheGlobalDriverPathIsCarriedWithoutBeingResolvedAgainstThisMachine()
    {
        var document = new NutConfigurationParser().Parse(
            NutConfigurationFileKind.UpsConf, "driverpath = C:\\NUT\\bin\n[NOBREAK]\ndriver = nutdrv_qx\nport = COM4\n");

        var driver = Assert.Single(NutRemoteConfiguredDriverReader.Read(document, [Port("COM4")]));

        Assert.Equal("C:\\NUT\\bin", driver.DriverPath);
        Assert.Null(driver.Executable.Path);
        Assert.False(driver.Executable.IsTrusted);
    }

    [Fact]
    public async Task TheRemotePortListIsOfferedToTheUpsConfEditorAsChoices()
    {
        // The detected ports feed the port field the same way local ones do. Writing still goes
        // through the graphical editor and the safe-write pipeline; nothing here writes anything.
        var client = new StubAgentClient { Ports = [Port("COM4")] };
        var pipeline = new StubPipeline("/etc/nut/ups.conf", "[NOBREAK]\ndriver = nutdrv_qx\nport = COM4\n");
        var viewModel = CreateRemoteViewModel(client, pipeline);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsComPortListKnown);
        Assert.Equal("COM4", Assert.Single(viewModel.ComPorts).PortName);
    }

    [Fact]
    public async Task AnUnreadUpsConfIsNotReportedAsAUpsConfWithNoDrivers()
    {
        // A remote profile reaches this state on every start: the agent answers about hardware without
        // a configuration session, so the ports are known long before anything can open ups.conf.
        // An empty driver list here means nobody looked, and saying "no configured driver was found in
        // ups.conf" would assert something about a file the application has never opened.
        var client = new StubAgentClient { Ports = [Port("COM4")] };
        var viewModel = CreateRemoteViewModel(client);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsComPortListKnown);
        Assert.Empty(viewModel.ConfiguredDrivers);
        Assert.False(viewModel.IsConfiguredDriverListKnown);
        Assert.True(viewModel.IsConfiguredDriverListUnknown);

        // The claim about the file's contents stays hidden while it is unread.
        Assert.False(viewModel.HasNoConfiguredDrivers);
    }

    [Fact]
    public async Task AUpsConfThatWasReadReportsItsContentsAsKnown()
    {
        // The other side of the same flag: read the file and the list becomes an answer, whether or
        // not it found anything. Without this the flag could sit false forever and the screen would
        // never state what it does know.
        var client = new StubAgentClient { Ports = [Port("COM4")] };
        var empty = new StubPipeline("/etc/nut/ups.conf", "# ups.conf with no driver sections");
        var viewModel = CreateRemoteViewModel(client, empty);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsConfiguredDriverListKnown);
        Assert.Empty(viewModel.ConfiguredDrivers);

        // Read, and it genuinely declares none: now the message may be shown.
        Assert.True(viewModel.HasNoConfiguredDrivers);
        Assert.False(viewModel.IsConfiguredDriverListUnknown);
    }

    // ---------------------------------------------------------------- source boundaries

    [Fact]
    public void TheInspectionPathNeverOpensAPortOrStartsAProcess()
    {
        // The whole capability, read as source. If any of these ever appears, the passive boundary
        // this task is built on has been crossed and the test says where.
        string[] files =
        [
            Path.Combine("src", "NutManager.Core", "Agent", "NutAgentApplicationService.cs"),
            Path.Combine("src", "NutManager.Core", "Agent", "NutAgentRequestDispatcher.cs"),
            Path.Combine("src", "NutManager.Core", "Administration", "NutSerialDeviceIdentity.cs"),
            Path.Combine("src", "NutManager.Core", "Administration", "NutRemoteConfiguredDriverReader.cs"),
            Path.Combine("src", "NutManager.Infrastructure", "Agent", "WindowsNutAgentHardwareInspector.cs")
        ];

        string[] forbidden =
        [
            "SerialPort", "CreateFile", "WriteFile", "ReadFile",
            "Process.Start", "ProcessStartInfo", "cmd.exe", "powershell", "netsh", "net.exe",
            "File.WriteAllText", "File.WriteAllBytes", "File.Delete"
        ];

        foreach (var file in files)
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), file));
            foreach (var token in forbidden)
            {
                Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheInspectorDelegatesToTheExistingPassiveSourceRatherThanEnumeratingAgain()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "NutManager.Infrastructure", "Agent", "WindowsNutAgentHardwareInspector.cs"));

        // It must use the COM source the local screen already uses, not a second enumeration that
        // would eventually disagree with it.
        Assert.Contains("IWindowsComPortSource", source, StringComparison.Ordinal);
        Assert.Contains("WindowsWmiComPortSource", source, StringComparison.Ordinal);
        // No enumeration of its own: no registry read and no WMI query live in this file.
        Assert.DoesNotContain("Registry.LocalMachine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSubKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ManagementObjectSearcher", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentContractStillCannotNameAPortASpeedOrACommand()
    {
        var names = typeof(NutAgentRequest).GetProperties().Select(property => property.Name).ToArray();

        Assert.Equal(["ProtocolVersion", "Operation", "OperationId"], names);
        foreach (var forbidden in new[] { "Port", "Baud", "Command", "Executable", "Arguments", "Path", "Payload" })
        {
            Assert.DoesNotContain(forbidden, names, StringComparer.OrdinalIgnoreCase);
        }
    }


    [Fact]
    public void EveryBindingOnTheDevicesViewResolvesToARealMember()
    {
        // Bindings inside a data template are resolved at runtime, so a renamed member fails silently
        // as a blank row rather than as a build error. This is the cheap guard against that.
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "NutManager.App", "Views", "DevicesDriversAdministrationView.axaml"));

        Type[] bound =
        [
            typeof(AdministrationPageViewModel),
            typeof(DetectedComPortViewModel),
            typeof(NutConfiguredDriver)
        ];
        var members = bound
            .SelectMany(type => type.GetProperties().Select(property => property.Name))
            .ToHashSet(StringComparer.Ordinal);

        // Simple paths only: an indexed localizer lookup and a dotted path are matched by neither the
        // pattern nor this assertion, and both are already exercised elsewhere.
        var names = System.Text.RegularExpressions.Regex
            .Matches(view, @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)\s*[,}]")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            Assert.True(members.Contains(name), $"The view binds '{name}', which no bound type exposes.");
        }
    }

    [Fact]
    public void TheDevicesViewOffersNoRemoteRouteToAnActiveDiagnostic()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "NutManager.App", "Views", "DevicesDriversAdministrationView.axaml"));

        // The diagnostic buttons exist exactly once, inside the card gated on local availability.
        var start = view.IndexOf("AreActiveDiagnosticsAvailable", StringComparison.Ordinal);
        Assert.True(start > 0);
        foreach (var handler in new[] { "UpsdrvctlHelpButton_OnClick", "DriverDataDumpButton_OnClick", "DriverHelpButton_OnClick" })
        {
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(view, handler));
            Assert.True(view.IndexOf(handler, StringComparison.Ordinal) > start);
        }
    }
    [Fact]
    public void AttachingTheConfigurationSessionRereadsTheConfiguredDrivers()
    {
        // A remote profile fills the devices screen before any configuration session exists, because
        // the agent answers without one. The driver read at that point finds no transport and yields
        // an empty list, and nothing re-runs it, so the drivers stay missing until the operator
        // presses Refresh — which is the bug this guards.
        //
        // The guard is over source text rather than behaviour, and that is a limitation rather than a
        // preference: the wiring is an async void handler on an event only its declaring class can
        // raise, so no test can drive it without opening a seam in production code. What this catches
        // is the call being deleted. It cannot catch it being called at the wrong moment.
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "NutManager.App", "ViewModels", "AdministrationPageViewModel.cs"));

        var handler = source.IndexOf("private async void OnRemoteConfigurationContextChanged", StringComparison.Ordinal);
        Assert.True(handler > 0, "The remote configuration context handler is gone or was renamed.");

        var attach = source.IndexOf("_configurationPipeline = pipeline;", handler, StringComparison.Ordinal);
        Assert.True(attach > handler, "The handler no longer attaches the pipeline.");

        var reread = source.IndexOf("LoadRemoteConfiguredDriversAsync", attach, StringComparison.Ordinal);
        Assert.True(
            reread > attach,
            "The handler attaches a configuration session without re-reading the configured drivers, " +
            "so a remote profile shows no drivers until Refresh is pressed.");
    }

    // ---------------------------------------------------------------- helpers

    private static NutManagerLocalizer Strings() => new(UiLanguagePreference.PtBr);

    private static NutComPortInfo Port(string name) => new(name, null, null, null, null, null, true);

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

    private static NutAgentApplicationService CreateAgent(
        INutAgentHardwareInspector? inspector,
        bool targetResolved,
        RecordingAudit? audit = null) =>
        new(new StubTargetResolver(targetResolved),
            new RefusingController(),
            audit ?? new RecordingAudit(),
            new PermissiveAuthorization(),
            TimeProvider.System,
            new NutAgentOptions { MachineName = "EXAMPLE-HOST" },
            inspector);

    private static AdministrationPageViewModel CreateLocalViewModel(
        INutManagerAgentClient client,
        params NutComPortInfo[] ports) =>
        new(null,
            null,
            null,
            new StubDriverDiagnostics(ports),
            CreateContext(NutManagementMode.Local),
            null,
            UiLanguagePreference.PtBr,
            null,
            null,
            null,
            client);

    /// <summary>
    /// A remote page with an agent client, and — when the test needs the configured drivers — a
    /// configuration pipeline with a remote ups.conf path already established, which is what a
    /// connected remote session produces.
    /// </summary>
    private static AdministrationPageViewModel CreateRemoteViewModel(
        INutManagerAgentClient client,
        StubPipeline? pipeline = null)
    {
        var viewModel = new AdministrationPageViewModel(
            null, pipeline, null, null, CreateContext(NutManagementMode.Remote),
            null, UiLanguagePreference.PtBr, null, null, null, client);

        if (pipeline is not null)
        {
            var upsConf = viewModel.ConfigurationFiles.Single(file => file.FileKind == NutConfigurationFileKind.UpsConf);
            upsConf.FullPath = pipeline.Path;
        }

        return viewModel;
    }

    private static ManagedNutServerRuntimeContext CreateContext(NutManagementMode mode)
    {
        var profile = new ManagedNutServerProfile(
            Guid.NewGuid(),
            "T38 profile",
            new NutMonitoringProfile("server.example", 3493, "NOBREAK"),
            mode == NutManagementMode.Local
                ? new NutManagementProfile(NutManagementMode.Local)
                : new NutManagementProfile(NutManagementMode.Remote, "server.example", "/etc/nut"),
            ManagedNutServerAccessMode.Manage);

        return ManagedNutServerRuntimeContext.FromProfiles(
            new ManagedNutServerProfiles(ManagedNutServerProfiles.CurrentSchemaVersion, profile.Id, [profile]),
            new ApplicationSettings());
    }

    // ---------------------------------------------------------------- fakes

    private sealed class RecordingInspector(params NutComPortInfo[] ports) : INutAgentHardwareInspector
    {
        private int _calls;

        public int Calls => _calls;

        public Task<NutAgentHardwareSnapshot> InspectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new NutAgentHardwareSnapshot(
                "EXAMPLE-HOST", ports, true, null, DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class ThrowingInspector : INutAgentHardwareInspector
    {
        public Task<NutAgentHardwareSnapshot> InspectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The device map could not be read.");
    }

    private sealed class StubTargetResolver(bool resolved) : INutServiceTargetResolver
    {
        private static readonly NutServiceTarget Target =
            new("NUT", "Network UPS Tools", @"C:\NUT\sbin\nut.exe", NutAssociationConfidence.BinaryPath);

        public Task<NutServiceTargetResolution> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(resolved
                ? new NutServiceTargetResolution(NutServiceTargetStatus.Resolved, Target)
                : new NutServiceTargetResolution(NutServiceTargetStatus.NotFound, null));

        public Task<NutServiceTargetResolution> RevalidateAsync(NutServiceTarget target, CancellationToken cancellationToken) =>
            ResolveAsync(cancellationToken);
    }

    /// <summary>Proves the read never touches service control: every method here is a failure.</summary>
    private sealed class RefusingController : INutServiceController
    {
        public Task<NutAgentServiceStatus> GetStatusAsync(NutServiceTarget target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hardware inspection must not read the service.");

        public Task<NutServiceControlOutcome> StartAsync(NutServiceTarget target, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hardware inspection must not control the service.");

        public Task<NutServiceControlOutcome> StopAsync(NutServiceTarget target, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hardware inspection must not control the service.");
    }

    private sealed class RecordingAudit : INutAgentAuditSink
    {
        public List<NutAgentAuditEntry> Entries { get; } = [];

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> WriteAsync(NutAgentAuditEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.FromResult(true);
        }
    }

    private sealed class PermissiveAuthorization : INutAgentAuthorization
    {
        public bool IsConfigured => true;

        public string? ConfigurationFailure => null;

        public Task<bool> IsAuthorizedAsync(string identity, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>
    /// Stands in for the agent on the other machine. It records which operations were asked for, which
    /// is how the tests assert that an unsupported one is never sent.
    /// </summary>
    private sealed class StubAgentClient : INutManagerAgentClient
    {
        public List<NutAgentOperation> Operations { get; } = [];

        public NutAgentClientStatus HandshakeStatus { get; init; } = NutAgentClientStatus.Success;

        public IReadOnlyList<NutAgentOperation> Capabilities { get; init; } =
            [NutAgentOperation.Handshake, NutAgentOperation.GetStatus, NutAgentOperation.GetHardwareSnapshot];

        public IReadOnlyList<NutComPortInfo> Ports { get; init; } = [];

        public bool EnumerationSucceeded { get; init; } = true;

        public Task<NutAgentClientResult<NutAgentHandshake>> HandshakeAsync(string host, CancellationToken cancellationToken)
        {
            Operations.Add(NutAgentOperation.Handshake);
            return Task.FromResult(HandshakeStatus == NutAgentClientStatus.Success
                ? NutAgentClientResult<NutAgentHandshake>.Ok(
                    new NutAgentHandshake(NutAgentOptions.ProtocolVersion, "1.0.0", "EXAMPLE-HOST", Capabilities, true, null),
                    NutAgentResultCode.Success)
                : NutAgentClientResult<NutAgentHandshake>.Failure(HandshakeStatus));
        }

        public Task<NutAgentClientResult<NutAgentServiceStatus>> GetStatusAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The devices page asks for hardware, never for service state.");

        public Task<NutAgentClientResult<NutAgentHardwareSnapshot>> GetHardwareSnapshotAsync(string host, CancellationToken cancellationToken)
        {
            Operations.Add(NutAgentOperation.GetHardwareSnapshot);
            return Task.FromResult(NutAgentClientResult<NutAgentHardwareSnapshot>.Ok(
                new NutAgentHardwareSnapshot(
                    "EXAMPLE-HOST",
                    EnumerationSucceeded ? Ports : [],
                    EnumerationSucceeded,
                    null,
                    DateTimeOffset.UnixEpoch),
                NutAgentResultCode.Success));
        }

        public Task<NutAgentClientResult<NutAgentOperationResult>> StartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Device inspection must never mutate the service.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> StopAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Device inspection must never mutate the service.");

        public Task<NutAgentClientResult<NutAgentOperationResult>> RestartAsync(string host, Guid operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Device inspection must never mutate the service.");
    }

    /// <summary>Local diagnostics with a fixed port list. It opens nothing.</summary>
    private sealed class StubDriverDiagnostics(IReadOnlyList<NutComPortInfo> ports) : ILocalNutDriverDiagnostics
    {
        public Task<NutDriverDiagnosticsSnapshot> InspectAsync(NutInstallationInfo installation, CancellationToken cancellationToken) =>
            Task.FromResult(new NutDriverDiagnosticsSnapshot(true, ports, [], null));

        public Task<NutDriverDiagnosticResult> ExecuteAsync(NutDriverDiagnosticRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No diagnostic is executed by these tests.");
    }

    /// <summary>
    /// Reads one document and refuses everything else. Prepare and Apply throw, which is how these
    /// tests assert that remote inspection has no route to a write.
    /// </summary>
    private sealed class StubPipeline(string path, string text) : INutConfigurationFilePipeline
    {
        public string Path => path;

        public int LoadCalls { get; private set; }

        public Task<NutConfigurationLoadResult> LoadAsync(
            string targetPath, NutConfigurationFileKind fileKind, CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            if (!string.Equals(targetPath, path, StringComparison.Ordinal))
            {
                return Task.FromResult(new NutConfigurationLoadResult(NutConfigurationLoadStatus.TargetNotFound));
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            return Task.FromResult(new NutConfigurationLoadResult(
                NutConfigurationLoadStatus.Success,
                new NutConfigurationFileSnapshot(
                    targetPath,
                    fileKind,
                    new NutConfigurationParser().Parse(fileKind, text),
                    NutConfigurationTextEncoding.Utf8,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                    bytes.LongLength)));
        }

        public NutConfigurationPreparedChange Prepare(NutConfigurationFileSnapshot snapshot) =>
            throw new InvalidOperationException("Device inspection must never prepare a configuration change.");

        public Task<NutConfigurationApplyResult> ApplyAsync(
            NutConfigurationPreparedChange change, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Device inspection must never write configuration.");
    }
}
