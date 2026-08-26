using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

        // Three, not five. The developer's own GitHub card and the licence card were removed: the
        // handle in the credit line reaches the profile, and the licence is one click further into
        // a repository already linked here.
        Assert.Equal(3, page.Resources.Count);
    }

    [Fact]
    public void OfficialDestinationsAreFixedAndReviewed()
    {
        Assert.Equal(
            "https://github.com/Marcelo-PX/NutManager",
            ExternalResourceCatalog.GetUri(ExternalResource.ProjectRepository)?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            "https://github.com/marcelodotnet",
            ExternalResourceCatalog.GetUri(ExternalResource.DeveloperProfile)?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            "https://marcelodotnet.notion.site/NutManager-T39-Installer-Packaging-Documentation-3c657ac07709810bb8d8ec798f82a942",
            ExternalResourceCatalog.GetUri(ExternalResource.OperatorManual)?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            "https://github.com/Marcelo-PX/NutManager/tree/main/docs",
            ExternalResourceCatalog.GetUri(ExternalResource.TechnicalDocumentation)?.AbsoluteUri.TrimEnd('/'));
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

    // ---------------------------------------------------------------- developer credit

    [Theory]
    [InlineData(UiLanguagePreference.PtBr, "Projeto desenvolvido e mantido por @marcelodotnet")]
    [InlineData(UiLanguagePreference.EnUs, "Project developed and maintained by @marcelodotnet")]
    public void TheCreditNamesTheDeveloperAndTheHandleInBothCultures(UiLanguagePreference language, string expected)
    {
        var page = new AboutPageViewModel(RuntimeInfo, new RecordingLauncher(), language);

        Assert.Equal("Marcelo Pacheco", page.DeveloperName);
        Assert.Equal("@marcelodotnet", page.DeveloperHandle);
        Assert.Equal(expected, page.DeveloperCreditText);

        // The handle is an identifier, so only the sentence around it is translated.
        Assert.EndsWith(page.DeveloperHandle, page.DeveloperCreditText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHandleOpensTheAllowlistedDeveloperProfileAndNothingElse()
    {
        var launcher = new RecordingLauncher();
        var page = new AboutPageViewModel(RuntimeInfo, launcher, UiLanguagePreference.PtBr);

        page.OpenDeveloperProfileCommand.Execute(null);

        Assert.Equal(ExternalResource.DeveloperProfile, launcher.LastOpened);

        // The command names a catalogue entry; the address is resolved behind the boundary and the
        // view never sees it. A literal URL in the AXAML is the regression worth catching.
        var view = Repository.Read(Path.Combine("src", "NutManager.App", "Views", "AboutPageView.axaml"));
        Assert.Contains("{Binding OpenDeveloperProfileCommand}", view, StringComparison.Ordinal);
        Assert.Contains("{Binding DeveloperHandle}", view, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigateUri", view, StringComparison.Ordinal);

        // Stated as an inventory rather than a "does not contain http": the two XML namespace
        // declarations are addresses too, and a blanket check would either fail on them or have to
        // be loosened until it stopped catching anything.
        Assert.Equal(
            new[] { "https://github.com/avaloniaui", "http://schemas.microsoft.com/winfx/2006/xaml" },
            Regex.Matches(view, "https?://[^\"\\s]+").Select(match => match.Value));
    }

    [Fact]
    public void TheDeveloperProfileSurvivesAsALinkEvenThoughItsCardIsGone()
    {
        // Removing the card must not remove the destination: the credit line is its only remaining
        // caller, and dropping the enum entry alongside the card would have broken it silently.
        Assert.NotNull(ExternalResourceCatalog.GetUri(ExternalResource.DeveloperProfile));
        Assert.DoesNotContain(
            ExternalResource.DeveloperProfile,
            new AboutPageViewModel(RuntimeInfo, new RecordingLauncher(), UiLanguagePreference.PtBr)
                .Resources.Select(resource => resource.Resource));
    }

    // ---------------------------------------------------------------- official resources

    [Fact]
    public void OnlyTheThreeRemainingResourceCardsAreOffered()
    {
        var page = new AboutPageViewModel(RuntimeInfo, new RecordingLauncher(), UiLanguagePreference.PtBr);

        Assert.Equal(
            new[]
            {
                ExternalResource.ProjectRepository,
                ExternalResource.OperatorManual,
                ExternalResource.TechnicalDocumentation
            },
            page.Resources.Select(resource => resource.Resource));

        Assert.All(page.Resources, resource => Assert.True(resource.IsAvailable));
    }

    [Fact]
    public void TheLicenceDestinationIsGoneRatherThanLeftUnreachable()
    {
        // A catalogue entry nothing can reach is worse than no entry: it reads as a supported
        // destination during review and can never be opened. Removed with its card, both together.
        Assert.DoesNotContain("License", Enum.GetNames<ExternalResource>());
        Assert.DoesNotContain(
            "About.Resource.License",
            NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.PtBr));
        Assert.DoesNotContain(
            "About.Resource.DeveloperGitHub",
            NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.PtBr));
    }

    // ---------------------------------------------------------------- merged surfaces

    [Fact]
    public void AboutPresentsInformationAndDeveloperOnOneCardAndNoTechnologyList()
    {
        var path = Path.Combine("src", "NutManager.App", "Views", "AboutPageView.axaml");
        var view = XDocument.Parse(Repository.Read(path));
        var raw = Repository.Read(path);

        Assert.Same(
            SingleCardShowing(view, "About.ApplicationInfo.Title"),
            SingleCardShowing(view, "About.Developer.Title"));

        // Both subsections keep their own heading inside that one card.
        Assert.Contains("About.ApplicationInfo.Title", raw, StringComparison.Ordinal);
        Assert.Contains("About.Developer.Title", raw, StringComparison.Ordinal);

        // The technology pills restated the runtime and platform named directly above them.
        Assert.DoesNotContain("About.Technologies", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("About.LicenseNotice", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Network UPS Tools", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsPresentsConnectionAndPollingOnOneCardWithoutTheApplicationCard()
    {
        var path = Path.Combine("src", "NutManager.App", "Views", "DiagnosticsPageView.axaml");
        var view = XDocument.Parse(Repository.Read(path));
        var raw = Repository.Read(path);

        // Both are still shown: this merge was layout, not a removal of state.
        Assert.Contains("Diagnostics.Group.Connection", raw, StringComparison.Ordinal);
        Assert.Contains("Diagnostics.Group.Polling", raw, StringComparison.Ordinal);
        Assert.Contains("{Binding Host}", raw, StringComparison.Ordinal);
        Assert.Contains("{Binding PollingIntervalText}", raw, StringComparison.Ordinal);
        Assert.Contains("{Binding ConnectionTimeoutText}", raw, StringComparison.Ordinal);

        // And on the same card, which is the part a string search cannot tell you.
        Assert.Same(
            SingleCardShowing(view, "Diagnostics.Group.Connection"),
            SingleCardShowing(view, "Diagnostics.Group.Polling"));

        // The application card and the group heading that carried it are both gone.
        Assert.DoesNotContain("Diagnostics.Group.Environment", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Diagnostics.Application", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding ApplicationVersion}", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding OperatingSystem}", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedKeysLeaveNoDanglingReferenceAndTheCulturesStayInStep()
    {
        var pt = NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.PtBr);
        var en = NutManagerLocalizer.GetAvailableKeys(UiLanguagePreference.EnUs);

        Assert.Equal(pt.Order(), en.Order());
        Assert.Contains("About.Developer.MaintainedBy", pt);

        foreach (var retired in new[]
                 {
                     "About.Technologies.Title", "About.LicenseNotice",
                     "About.Developer.Role", "About.Developer.Description",
                     "Diagnostics.Group.Environment", "Diagnostics.Application",
                     "Diagnostics.Runtime", "Diagnostics.Architecture"
                 })
        {
            Assert.DoesNotContain(retired, pt);
            Assert.DoesNotContain(retired, en);
        }
    }

    /// <summary>
    /// The one card whose subtree mentions <paramref name="marker"/>. Structural rather than
    /// positional: two headings can sit any distance apart in the file and still share a card, and a
    /// character-offset check would call that a failure.
    /// </summary>
    private static XElement SingleCardShowing(XDocument document, string marker)
    {
        var cards = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Border" &&
                ((string?)element.Attribute("Classes"))?.Split(' ').Contains("nut-card") == true)
            .ToArray();

        return Assert.Single(cards, card => card
            .DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Any(attribute => attribute.Value.Contains(marker, StringComparison.Ordinal)));
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
