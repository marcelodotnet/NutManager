namespace NutManager.Core.Services;

/// <summary>Known, product-owned destinations that the desktop application may open.</summary>
public enum ExternalResource
{
    ProjectRepository,
    DeveloperProfile,
    OperatorManual,
    TechnicalDocumentation
}

public enum ExternalResourceOpenResult
{
    Opened,
    Unavailable,
    Failed
}

/// <summary>
/// Opens only a known resource. The boundary deliberately accepts no URI or free-form string.
/// </summary>
public interface IExternalResourceLauncher
{
    bool IsAvailable(ExternalResource resource);

    ExternalResourceOpenResult Open(ExternalResource resource);
}
