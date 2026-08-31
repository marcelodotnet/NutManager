using System.Text.Json;
using System.Text.Json.Serialization;
using NutManager.Core.Models;

namespace NutManager.Agent.Config.Localization;

/// <summary>
/// Where this window remembers which language an operator chose.
///
/// Deliberately not <c>agent.json</c>. That file is the service's configuration: it is read by a
/// process running as SYSTEM, it lives under ProgramData with an ACL that admits only SYSTEM and
/// Administrators, and every write to it goes through the safe-write pipeline. Which language an
/// administrator prefers to read is none of those things - it changes nothing about how the agent
/// listens, and putting it there would mean a view preference riding an administrative write.
///
/// So it goes in the user's own profile, unprivileged and per-user, which is also the honest scope:
/// two administrators on one server can disagree about this without either overruling the other.
/// </summary>
public interface IAgentConfigUiPreferences
{
    /// <summary>The saved language, or null when nothing has been saved or it cannot be read.</summary>
    UiLanguagePreference? ReadLanguage();

    void WriteLanguage(UiLanguagePreference language);

    /// <summary>
    /// The saved theme, or null when nobody has chosen one.
    ///
    /// Null is not the same as System. Null means the question has never been answered, so the window
    /// follows whatever the desktop application's own default rule does; System would be a choice
    /// somebody made. Only an explicit click writes here.
    /// </summary>
    ThemePreference? ReadTheme();

    void WriteTheme(ThemePreference theme);
}

/// <summary>The file-backed preference store, plus a no-op for tests and for design-time.</summary>
public sealed class AgentConfigUiPreferences : IAgentConfigUiPreferences
{
    /// <summary>
    /// Remembers nothing. The default when no store is supplied, so a view model constructed in a
    /// test never reads or writes the profile of whoever is running the test.
    /// </summary>
    public static IAgentConfigUiPreferences None { get; } = new NoPreferences();

    private readonly string _path;

    public AgentConfigUiPreferences()
        : this(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NutManager",
            "agent-config-ui.json"))
    {
    }

    internal AgentConfigUiPreferences(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public UiLanguagePreference? ReadLanguage() =>
        // An unrecognised tag reads as "nothing saved" rather than as pt-BR, so a file written by a
        // later version that adds a language does not silently pin this one to the wrong one.
        Read()?.Language switch
        {
            "en-US" => UiLanguagePreference.EnUs,
            "pt-BR" => UiLanguagePreference.PtBr,
            _ => null,
        };

    public ThemePreference? ReadTheme() => Read()?.Theme switch
    {
        "light" => ThemePreference.Light,
        "dark" => ThemePreference.Dark,
        _ => null,
    };

    public void WriteLanguage(UiLanguagePreference language) =>
        Write(current => current with
        {
            Language = language == UiLanguagePreference.EnUs ? "en-US" : "pt-BR",
        });

    public void WriteTheme(ThemePreference theme) =>
        Write(current => current with
        {
            // Only the two an operator can actually pick are written. System is the absence of a
            // choice, and it is represented by the field being absent rather than by a third tag.
            Theme = theme == ThemePreference.Light ? "light" : "dark",
        });

    private AgentConfigUiDocument? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            return JsonSerializer.Deserialize(
                File.ReadAllText(_path), AgentConfigUiPreferencesJson.Default.AgentConfigUiDocument);
        }
        catch (Exception)
        {
            // A preference file that cannot be read is a preference nobody set. The window falls back
            // to its defaults and carries on: this is a convenience, and it must never be the reason
            // an administration utility refuses to open.
            return null;
        }
    }

    /// <summary>
    /// Read, change one field, write the whole file back.
    ///
    /// Read-modify-write rather than write-what-I-know, because the two preferences are set from
    /// different controls at different moments: serialising only the field being changed would drop
    /// the other one every time somebody switched language or theme.
    /// </summary>
    private void Write(Func<AgentConfigUiDocument, AgentConfigUiDocument> change)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(
                    change(Read() ?? new AgentConfigUiDocument(null, null)),
                    AgentConfigUiPreferencesJson.Default.AgentConfigUiDocument));
        }
        catch (Exception)
        {
            // Same reasoning as the read. The preference has already taken effect on screen; failing
            // to remember it for next time is not worth interrupting an administrator over.
        }
    }

    private sealed class NoPreferences : IAgentConfigUiPreferences
    {
        public UiLanguagePreference? ReadLanguage() => null;

        public void WriteLanguage(UiLanguagePreference language)
        {
        }

        public ThemePreference? ReadTheme() => null;

        public void WriteTheme(ThemePreference theme)
        {
        }
    }
}

/// <summary>
/// The whole file: a culture tag and a theme tag, both optional. Never a credential, a path or a
/// machine fact, and never anything the Agent service reads.
/// </summary>
public sealed record AgentConfigUiDocument(
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("theme")] string? Theme);

// Absent rather than null: a preference nobody has set should leave no trace in the file, so a
// language-only document stays a language-only document.
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentConfigUiDocument))]
internal sealed partial class AgentConfigUiPreferencesJson : JsonSerializerContext;
