using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NutManager.App.Localization;
using NutManager.App.Services;
using NutManager.Core.Models;
using NutManager.Core.Services;

namespace NutManager.App.ViewModels;

public sealed partial class AboutPageViewModel : PageViewModel
{
    private readonly ApplicationRuntimeInfo _runtimeInfo;

    public AboutPageViewModel(
        ApplicationRuntimeInfo runtimeInfo,
        IExternalResourceLauncher externalResourceLauncher,
        UiLanguagePreference language = UiLanguagePreference.PtBr)
        : base(new NutManagerLocalizer(language).Get("About.Title"), new NutManagerLocalizer(language).Get("About.Description"))
    {
        ArgumentNullException.ThrowIfNull(runtimeInfo);
        ArgumentNullException.ThrowIfNull(externalResourceLauncher);

        _runtimeInfo = runtimeInfo;
        Strings = new NutManagerLocalizer(language);
        Resources = CreateResources(externalResourceLauncher);

        // The credit line is a link to one reviewed destination, not a link to a stored address.
        // Routing it through the same launcher as the resource cards is what keeps the UI from
        // ever holding a URL of its own.
        OpenDeveloperProfileCommand = new RelayCommand(
            () => OpenResource(externalResourceLauncher, ExternalResource.DeveloperProfile),
            () => externalResourceLauncher.IsAvailable(ExternalResource.DeveloperProfile));
    }

    public NutManagerLocalizer Strings { get; }
    public string ProductName => Strings.Get("App.Name");
    public string ProductDescription => Strings.Get("About.ProductDescription");
    public string DeveloperName => "Marcelo Pacheco";

    /// <summary>
    /// A handle is an identifier, not prose: it reads the same in both cultures and is not
    /// localized, for the same reason a driver name or a status token is not.
    /// </summary>
    public string DeveloperHandle => "@marcelodotnet";

    public string DeveloperCreditPrefix => Strings.Get("About.Developer.MaintainedBy");

    /// <summary>The whole sentence, for assistive technology and for tests.</summary>
    public string DeveloperCreditText => $"{DeveloperCreditPrefix} {DeveloperHandle}";

    public ICommand OpenDeveloperProfileCommand { get; }
    public string DisplayVersion => _runtimeInfo.DisplayVersion;
    public string BuildIdentifier => _runtimeInfo.BuildIdentifier;
    public string Platform => _runtimeInfo.OperatingSystem;
    public string Architecture => _runtimeInfo.Architecture;
    public string Runtime => _runtimeInfo.Runtime;
    public IReadOnlyList<AboutResourceViewModel> Resources { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResourceStatusMessage))]
    private string? _resourceStatusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyStatusMessage))]
    private string? _copyStatusMessage;

    public bool HasResourceStatusMessage => !string.IsNullOrWhiteSpace(ResourceStatusMessage);
    public bool HasCopyStatusMessage => !string.IsNullOrWhiteSpace(CopyStatusMessage);

    public string CreateSupportInformation()
    {
        var report = new StringBuilder();
        report.AppendLine(ProductName);
        report.Append(Strings.Get("About.Version")).Append(": ").AppendLine(_runtimeInfo.Version);
        report.Append(Strings.Get("About.Build")).Append(": ").AppendLine(BuildIdentifier);
        report.Append(Strings.Get("About.Platform")).Append(": ").AppendLine(Platform);
        report.Append(Strings.Get("About.Architecture")).Append(": ").AppendLine(Architecture);
        report.Append(Strings.Get("About.Runtime")).Append(": ").Append(Runtime);
        return report.ToString();
    }

    public void ReportCopyResult(bool succeeded) =>
        CopyStatusMessage = Strings.Get(succeeded ? "About.CopySucceeded" : "About.CopyFailed");

    private IReadOnlyList<AboutResourceViewModel> CreateResources(IExternalResourceLauncher launcher) =>
    [
        CreateResource(ExternalResource.ProjectRepository, "ProjectGitHub", launcher),
        CreateResource(ExternalResource.OperatorManual, "OperatorManual", launcher),
        CreateResource(ExternalResource.TechnicalDocumentation, "TechnicalDocumentation", launcher)
    ];

    private AboutResourceViewModel CreateResource(
        ExternalResource resource,
        string localizationSuffix,
        IExternalResourceLauncher launcher)
    {
        var available = launcher.IsAvailable(resource);
        return new AboutResourceViewModel(
            resource,
            Strings.Get($"About.Resource.{localizationSuffix}"),
            Strings.Get($"About.Resource.{localizationSuffix}.Description"),
            Strings.Get(available ? "About.Resource.Open" : "About.Resource.Pending"),
            available,
            () => OpenResource(launcher, resource));
    }

    private void OpenResource(IExternalResourceLauncher launcher, ExternalResource resource) =>
        ResourceStatusMessage = launcher.Open(resource) switch
        {
            ExternalResourceOpenResult.Failed => Strings.Get("About.Resource.OpenFailed"),
            ExternalResourceOpenResult.Unavailable => Strings.Get("About.Resource.PendingMessage"),
            _ => null
        };
}

public sealed class AboutResourceViewModel
{
    public AboutResourceViewModel(
        ExternalResource resource,
        string title,
        string description,
        string actionText,
        bool isAvailable,
        Action open)
    {
        Resource = resource;
        Title = title;
        Description = description;
        ActionText = actionText;
        IsAvailable = isAvailable;
        OpenCommand = new RelayCommand(open, () => isAvailable);
    }

    public ExternalResource Resource { get; }
    public string Title { get; }
    public string Description { get; }
    public string ActionText { get; }
    public bool IsAvailable { get; }
    public ICommand OpenCommand { get; }
}
