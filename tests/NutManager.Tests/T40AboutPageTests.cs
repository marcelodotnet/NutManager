using System.Reflection;
using NutManager.App.Localization;
using NutManager.App.Services;
using NutManager.App.ViewModels;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Infrastructure.Platform.Windows;
using Xunit;

namespace NutManager.Tests;

public sealed class T40AboutPageTests
{
    private static readonly ApplicationRuntimeInfo RuntimeInfo = new(
        "1.4.0+abcdef0123456789",
        "v1.4.0",
        ".NET 10 test runtime",
        "Windows test platform",
        "X64");

    [Fact]
    public void AboutUsesTheSharedFriendlyVersionAndRuntimeSource()
    {
        var page = new AboutPageViewModel(RuntimeInfo, new RecordingLauncher(), UiLanguagePreference.PtBr);

        Assert.Equal("v1.4.0", page.DisplayVersion);
        Assert.Equal("abcdef0123456789", page.BuildIdentifier);
        Assert.Equal(RuntimeInfo.Runtime, page.Runtime);
        Assert.Equal(RuntimeInfo.OperatingSystem, page.Platform);
        Assert.Equal(RuntimeInfo.Architecture, page.Architecture);
        Assert.Equal("abcdef0", ApplicationRuntimeInfo.FormatBuildIdentifier("1.0.0+abcdef0"));
        Assert.Equal("Indisponível", ApplicationRuntimeInfo.FormatBuildIdentifier("1.0.0"));
    }

    [Theory]
    [InlineData(UiLanguagePreference.PtBr, "Sobre", "Recursos oficiais")]
    [InlineData(UiLanguagePreference.EnUs, "About", "Official resources")]
    public void AboutIsLocalizedInBothOfficialCultures(
        UiLanguagePreference language,
        string expectedTitle,
        string expectedResourcesTitle)
    {
        var page = new AboutPageViewModel(RuntimeInfo, new RecordingLauncher(), language);

        Assert.Equal(expectedTitle, page.Title);
        Assert.Equal(expectedResourcesTitle, page.Strings.Get("About.Resources.Title"));
        Assert.Equal(5, page.Resources.Count);
    }

    [Fact]
    public void OfficialDestinationsAreFixedAndReviewed()
    {
        Assert.Equal(
            "https://github.com/Marcelo-PX/NutManager",
            ExternalResourceCatalog.GetUri(ExternalResource.ProjectRepository)?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            "https://github.com/Marcelo-PX",
            ExternalResourceCatalog.GetUri(ExternalResource.DeveloperProfile)?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            "https://marcelodotnet.notion.site/NutManager-T39-Installer-Packaging-Documentation-3c657ac07709810bb8d8ec798f82a942",
            ExternalResourceCatalog.GetUri(ExternalResource.OperatorManual)?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            "https://github.com/Marcelo-PX/NutManager/tree/main/docs",
            ExternalResourceCatalog.GetUri(ExternalResource.TechnicalDocumentation)?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            "https://github.com/Marcelo-PX/NutManager/blob/main/LICENSE",
            ExternalResourceCatalog.GetUri(ExternalResource.License)?.AbsoluteUri.TrimEnd('/'));
        Assert.Null(ExternalResourceCatalog.GetUri((ExternalResource)int.MaxValue));
    }

    [Fact]
    public void LauncherBoundaryCannotReceiveAnArbitraryUriOrString()
    {
        var boundaryMethods = typeof(IExternalResourceLauncher).GetMethods(BindingFlags.Instance | BindingFlags.Public);

        Assert.NotEmpty(boundaryMethods);
        Assert.All(boundaryMethods, method =>
            Assert.All(method.GetParameters(), parameter => Assert.Equal(typeof(ExternalResource), parameter.ParameterType)));
        Assert.DoesNotContain(
            typeof(WindowsExternalResourceLauncher).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(Uri)));
    }

    [Fact]
    public void ResourceCommandsPassOnlyTheSelectedAllowlistedDestination()
    {
        var launcher = new RecordingLauncher();
        var page = new AboutPageViewModel(RuntimeInfo, launcher, UiLanguagePreference.PtBr);
        var manual = page.Resources.Single(item => item.Resource == ExternalResource.OperatorManual);

        manual.OpenCommand.Execute(null);

        Assert.Equal(ExternalResource.OperatorManual, launcher.LastOpened);
    }

    [Fact]
    public void CopiedSupportInformationContainsOnlyRuntimeIdentification()
    {
        var page = new AboutPageViewModel(RuntimeInfo, new RecordingLauncher(), UiLanguagePreference.EnUs);

        var report = page.CreateSupportInformation();

        Assert.Contains(RuntimeInfo.Version, report, StringComparison.Ordinal);
        Assert.Contains(RuntimeInfo.Runtime, report, StringComparison.Ordinal);
        Assert.Contains(RuntimeInfo.OperatingSystem, report, StringComparison.Ordinal);
        Assert.Contains(RuntimeInfo.Architecture, report, StringComparison.Ordinal);
        Assert.DoesNotContain("password", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AboutViewKeepsResourcesExternalAndUsesTheOfficialApplicationAsset()
    {
        var view = Repository.Read(Path.Combine("src", "NutManager.App", "Views", "AboutPageView.axaml"));
        var app = Repository.Read(Path.Combine("src", "NutManager.App", "App.axaml"));

        Assert.Contains("/Assets/Branding/NutManager.png", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Resources}\"", view, StringComparison.Ordinal);
        Assert.Contains("CopySupportInformationButton_OnClick", view, StringComparison.Ordinal);
        Assert.Contains("DataType=\"viewModels:AboutPageViewModel\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView", view, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingLauncher : IExternalResourceLauncher
    {
        public ExternalResource? LastOpened { get; private set; }

        public bool IsAvailable(ExternalResource resource) => ExternalResourceCatalog.GetUri(resource) is not null;

        public ExternalResourceOpenResult Open(ExternalResource resource)
        {
            LastOpened = resource;
            return IsAvailable(resource) ? ExternalResourceOpenResult.Opened : ExternalResourceOpenResult.Unavailable;
        }
    }
}
