using System.Reflection;
using System.Runtime.InteropServices;

namespace NutManager.App.Services;

/// <param name="Version">
/// The technical version, including whatever build metadata the compiler stamped after a "+". Kept for
/// the copied diagnostic report, where identifying the exact build is the whole point.
/// </param>
/// <param name="DisplayVersion">
/// The same version as a person reads it: "v1.0.0". This is what the interface shows.
/// </param>
public sealed record ApplicationRuntimeInfo(
    string Version,
    string DisplayVersion,
    string Runtime,
    string OperatingSystem,
    string Architecture)
{
    private const string UnavailableText = "Indisponível";

    public static ApplicationRuntimeInfo CreateCurrent()
    {
        var assembly = typeof(ApplicationRuntimeInfo).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? UnavailableText
            : informationalVersion;

        return new ApplicationRuntimeInfo(
            version,
            FormatDisplayVersion(version),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString());
    }

    /// <summary>
    /// Turns a technical version into the one the interface shows.
    ///
    /// The informational version carries build metadata after a "+" — a full commit hash, in this
    /// build. That is genuinely useful to support and genuinely useless on screen: forty hexadecimal
    /// characters beside "Versão" tell a user nothing and push everything else out of the card.
    ///
    /// So the metadata is cut and a "v" is added, giving "v1.0.0". A pre-release suffix after a "-"
    /// survives, because "v1.1.0-rc.1" is something a user needs to see. Nothing here parses or
    /// re-derives the version; it only trims what the build already produced, which is what keeps this
    /// from becoming a second version source.
    /// </summary>
    public static string FormatDisplayVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return UnavailableText;

        var trimmed = version.Trim();
        var metadata = trimmed.IndexOf('+');
        if (metadata >= 0) trimmed = trimmed[..metadata];

        trimmed = trimmed.TrimEnd();
        if (trimmed.Length == 0) return UnavailableText;

        return trimmed.StartsWith('v') ? trimmed : "v" + trimmed;
    }
}
