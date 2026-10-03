using System.IO;
using System.Text.Json;

namespace TriAsr.App;

/// <summary>What the person chose for the notes. It has a small file of its own (<c>Config/notes.json</c>), the same in Studio and in the Client.</summary>
/// <param name="Language"><c>auto</c>, a language code, or two joined with <c>+</c> (<c>en+hu</c>), for the words that are dictated into a note.</param>
/// <param name="Hotkey">The keys that start and stop recording a note from any program, as <see cref="KeyCombo.Id"/> writes them; empty for none (the usual: the person turns it on by choosing keys).</param>
/// <param name="Microphone">The number of the microphone, or -1 for the one Windows uses.</param>
/// <param name="Left">Where the little window was left on the screen; null until it has been moved.</param>
public sealed record NotesSettings(string Language = "auto", string Hotkey = "", int Microphone = -1, double? Left = null, double? Top = null)
{
    public static NotesSettings Normalize(NotesSettings? loaded)
    {
        loaded ??= new();
        return loaded with
        {
            Language = string.IsNullOrWhiteSpace(loaded.Language) ? "auto" : loaded.Language,
            Hotkey = KeyCombo.Normalize(loaded.Hotkey, ""),
            Left = loaded.Left is { } left && double.IsFinite(left) ? left : null,
            Top = loaded.Top is { } top && double.IsFinite(top) ? top : null
        };
    }
}

/// <param name="dataRoot">The program's data folder; the file lies in its Config folder.</param>
public sealed class NotesSettingsStore(string dataRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public string FilePath => Path.Combine(dataRoot, "Config", "notes.json");

    /// <summary>What was saved; the defaults when nothing was saved or the file cannot be read.</summary>
    public NotesSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return NotesSettings.Normalize(JsonSerializer.Deserialize<NotesSettings>(stream, Json));
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }
    }

    /// <summary>Saves the choices. A failure to save is not worth stopping the notes for, so it is reported as false.</summary>
    public bool Save(NotesSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(NotesSettings.Normalize(settings), Json));
                File.Move(temporary, FilePath, true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}
