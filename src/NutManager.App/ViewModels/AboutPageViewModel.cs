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
    }

    public NutManagerLocalizer Strings { get; }
    public string ProductName => Strings.Get("App.Name");
    public string ProductDescription => Strings.Get("About.ProductDescription");
    public string DeveloperName => "Marcelo Pacheco";
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
        CreateResource(ExternalResource.DeveloperProfile, "DeveloperGitHub", launcher),
        CreateResource(ExternalResource.OperatorManual, "OperatorManual", launcher),
        CreateResource(ExternalResource.TechnicalDocumentation, "TechnicalDocumentation", launcher),
        CreateResource(ExternalResource.License, "License", launcher)
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
            () =>
            {
                var result = launcher.Open(resource);
                ResourceStatusMessage = result switch
                {
                    ExternalResourceOpenResult.Failed => Strings.Get("About.Resource.OpenFailed"),
                    ExternalResourceOpenResult.Unavailable => Strings.Get("About.Resource.PendingMessage"),
                    _ => null
                };
            });
    }
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
