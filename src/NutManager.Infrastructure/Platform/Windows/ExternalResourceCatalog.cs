using System.Diagnostics;
using NutManager.Core.Services;

namespace NutManager.Infrastructure.Platform.Windows;

/// <summary>Fixed destinations reviewed with the application. No profile or UI value enters here.</summary>
public static class ExternalResourceCatalog
{
    public static Uri? GetUri(ExternalResource resource) => resource switch
    {
        ExternalResource.ProjectRepository => new("https://github.com/Marcelo-PX/NutManager"),
        ExternalResource.DeveloperProfile => new("https://github.com/marcelodotnet"),
        ExternalResource.OperatorManual => new("https://marcelodotnet.notion.site/NutManager-T39-Installer-Packaging-Documentation-3c657ac07709810bb8d8ec798f82a942"),
        ExternalResource.TechnicalDocumentation => new("https://github.com/Marcelo-PX/NutManager/tree/main/docs"),
        _ => null
    };
}

public sealed class WindowsExternalResourceLauncher : IExternalResourceLauncher
{
    public bool IsAvailable(ExternalResource resource) => ExternalResourceCatalog.GetUri(resource) is not null;

    public ExternalResourceOpenResult Open(ExternalResource resource)
    {
        var uri = ExternalResourceCatalog.GetUri(resource);
        if (uri is null) return ExternalResourceOpenResult.Unavailable;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return ExternalResourceOpenResult.Opened;
        }
        catch (Exception)
        {
            return ExternalResourceOpenResult.Failed;
        }
    }
}
