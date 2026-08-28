using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NutManager.Agent.Config.Presentation;

/// <summary>
/// The few mappings the view needs and the view model must not hold.
///
/// All of them resolve a theme resource, which is a presentation concern: a view model producing an
/// <see cref="IBrush"/> would need an initialised Avalonia application to be constructed, and would
/// drag every test of it into needing one too. So the view model carries a semantic class name —
/// "healthy", "warning", "muted", "critical" — and this turns that into whatever the product's palette
/// currently says those mean.
///
/// There is deliberately no converter here that produces localized text. Text belongs to the view
/// model, which knows what language the window is in; a static converter would need a language of its
/// own to reach for.
/// </summary>
public static class AgentConfigConverters
{
    /// <summary>A semantic state class to the brush the product uses for it.</summary>
    public static readonly IValueConverter StateBrush =
        new FuncValueConverter<string?, IBrush?>(state => Resource<IBrush>(state switch
        {
            "healthy" => "NutHealthyBrush",
            "warning" => "NutWarningBrush",
            "critical" => "NutCriticalBrush",
            _ => "NutTextMutedBrush",
        }));

    /// <summary>
    /// An apply result to its colour. Failure is critical; anything else is ordinary secondary text —
    /// a successful save should not shout.
    /// </summary>
    public static readonly IValueConverter MessageBrush =
        new FuncValueConverter<bool, IBrush?>(failed =>
            Resource<IBrush>(failed ? "NutCriticalBrush" : "NutTextSecondaryBrush"));

    /// <summary>The tick or the warning triangle beside the certificate verdict.</summary>
    public static readonly IValueConverter VerdictIcon =
        new FuncValueConverter<bool, Geometry?>(valid =>
            Resource<Geometry>(valid ? "NutIconSuccess" : "NutIconWarning"));

    /// <summary>
    /// A catalog key to its drawing.
    ///
    /// This is what lets a status row carry its own icon without the view model ever holding a
    /// Geometry: the row names the glyph, and resolution happens here, where an Avalonia application
    /// exists. It is also why such a row can be constructed in a test with no UI at all.
    /// </summary>
    public static readonly IValueConverter IconByKey =
        new FuncValueConverter<string?, Geometry?>(key =>
            string.IsNullOrWhiteSpace(key) ? null : Resource<Geometry>(key));

    /// <summary>
    /// Looks the key up in the application's merged dictionaries, which is where the linked NutManager
    /// theme files put it. A missing key returns null rather than throwing: an uncoloured glyph is a
    /// cosmetic fault, and taking the window down over one would not be.
    /// </summary>
    private static T? Resource<T>(string key) where T : class
    {
        if (Application.Current is not { } application) return null;

        return application.TryGetResource(key, application.ActualThemeVariant, out var value) && value is T typed
            ? typed
            : null;
    }
}
