using NutManager.App.Localization;

namespace NutManager.App.ViewModels;

public enum AdministrationSection
{
    NutConfiguration,
    WindowsService,
    DevicesAndDrivers,
    RemoteAccess
}

public sealed record AdministrationSectionItemViewModel(
    AdministrationSection Section,
    string Title,
    string Description,
    bool IsApplicable,
    string AvailabilityText)
{
    // Per-section flags let the navigation rail pick a shared vector glyph without a converter.
    public bool IsNutConfiguration => Section == AdministrationSection.NutConfiguration;
    public bool IsWindowsService => Section == AdministrationSection.WindowsService;
    public bool IsDevicesAndDrivers => Section == AdministrationSection.DevicesAndDrivers;
    public bool IsRemoteAccess => Section == AdministrationSection.RemoteAccess;
}

public static class AdministrationPresentation
{
    public static IReadOnlyList<AdministrationSectionItemViewModel> CreateSections(
        NutManagerLocalizer strings,
        bool isRemote,
        bool canManage)
    {
        ArgumentNullException.ThrowIfNull(strings);
        // Devices and drivers reads through the agent on a remote profile, exactly as the service
        // section does. It was local-only before T38 and correctly said so; a profile that now lists
        // the server's serial ports and relates them to its ups.conf must not be told the section
        // belongs to local management. What stays local is the active diagnostics, and the section
        // itself says which half is which.
        var deviceAvailability = isRemote
            ? strings.Get("Administration.Availability.ViaAgent")
            : strings.Get("Administration.Availability.Available");

        // The NUT service is reachable on a remote profile, through the agent — which is the entire
        // reason the agent exists. This section was local-only before T35 and correctly said so; a
        // profile that now reads the remote service's state and pid, and is offered start, stop and
        // restart, must not be told the section belongs to local management.
        var serviceAvailability = isRemote
            ? strings.Get("Administration.Availability.ViaAgent")
            : strings.Get("Administration.Availability.Available");
        var remoteAvailability = isRemote
            ? strings.Get("Administration.Availability.Available")
            : strings.Get("Administration.Availability.RemoteOnly");
        var manageAvailability = canManage
            ? strings.Get("Administration.Availability.Manage")
            : strings.Get("Administration.Availability.ReadOnly");

        return
        [
            new(AdministrationSection.NutConfiguration,
                strings.Get("Administration.Section.Configuration"),
                strings.Get("Administration.Section.Configuration.Description"),
                true,
                manageAvailability),
            new(AdministrationSection.WindowsService,
                strings.Get("Administration.Section.WindowsService"),
                strings.Get("Administration.Section.WindowsService.Description"),
                true,
                serviceAvailability),
            new(AdministrationSection.DevicesAndDrivers,
                strings.Get("Administration.Section.DevicesDrivers"),
                strings.Get("Administration.Section.DevicesDrivers.Description"),
                true,
                deviceAvailability),
            new(AdministrationSection.RemoteAccess,
                strings.Get("Administration.Section.RemoteAccess"),
                strings.Get("Administration.Section.RemoteAccess.Description"),
                isRemote,
                remoteAvailability)
        ];
    }
}
