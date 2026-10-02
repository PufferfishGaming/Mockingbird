using System.IO;
using System.Text.Json;

namespace TriAsr.App;

/// <summary>What the person chose for live dictation. It has a small file of its own (<c>Config/dictation.json</c>), the same in Studio and in the Client.</summary>
/// <param name="Language">A language code or <c>auto</c>.</param>
/// <param name="Hotkey">The id of one of <see cref="HotkeyChoice.All"/>.</param>
/// <param name="Method"><c>type</c> (the words are typed key by key) or <c>paste</c> (they are put on the clipboard and pasted).</param>
/// <param name="Microphone">The number of the microphone, or -1 for the one Windows uses.</param>
/// <param name="Left">Where the little window was left on the screen; null until it has been moved.</param>
public sealed record DictationSettings(string Language = "auto", string Hotkey = "ctrl-alt-space", string Method = "type", int Microphone = -1, double? Left = null, double? Top = null)
{
    public static DictationSettings Normalize(DictationSettings? loaded)
    {
        loaded ??= new();
        return loaded with
        {
            Language = string.IsNullOrWhiteSpace(loaded.Language) ? "auto" : loaded.Language,
            Hotkey = HotkeyChoice.All.Any(choice => choice.Id == loaded.Hotkey) ? loaded.Hotkey : "ctrl-alt-space",
            Method = loaded.Method is "type" or "paste" ? loaded.Method : "type",
            Left = loaded.Left is { } left && double.IsFinite(left) ? left : null,
            Top = loaded.Top is { } top && double.IsFinite(top) ? top : null
        };
    }
}

/// <param name="dataRoot">The program's data folder; the file lies in its Config folder.</param>
public sealed class DictationSettingsStore(string dataRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public string FilePath => Path.Combine(dataRoot, "Config", "dictation.json");

    /// <summary>What was saved; the defaults when nothing was saved or the file cannot be read.</summary>
    public DictationSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return DictationSettings.Normalize(JsonSerializer.Deserialize<DictationSettings>(stream, Json));
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }
    }

    /// <summary>Saves the choices. A failure to save is not worth stopping dictation for, so it is reported as false.</summary>
    public bool Save(DictationSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(DictationSettings.Normalize(settings), Json));
                File.Move(temporary, FilePath, true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}
