using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TriAsr.Application;

namespace TriAsr.App;

/// <remarks>Version 2 turned the correction model on by default. A file saved by version 1 holds the old default, not a choice (it was never on), so it moves to the new default once.</remarks>
public sealed record AppSettings(string Theme = "System", string Density = "Comfortable", int Version = AppSettings.CurrentVersion, bool AnimateErrors = true,
    bool CheckForUpdates = true, string? SkippedUpdateVersion = null, DateTimeOffset? LastUpdateCheckUtc = null, string ResourceProfile = "Auto",
    bool SkipNonSpeech = false, bool UseCorrectionModel = true, string SetupState = SetupPlan.Pending,
    string WatchFolder = "", bool WatchEnabled = false, string WatchLanguage = "auto", string WatchOutput = "Text (.txt)")
{
    public const int CurrentVersion = 2;
}

public sealed class SettingsStore(IStoragePaths paths, ILogger<SettingsStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string FilePath => Path.Combine(paths.Root, "Config", "settings.json");
    public string? LastLoadError { get; private set; }

    public async Task<AppSettings> LoadAsync()
    {
        LastLoadError = null;
        try
        {
            if (!File.Exists(FilePath)) return new();
            // Readers must never block the atomic replace performed by a save, so they share the file for writing and replacing.
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _json) ?? new();
            return settings with
            {
                Version = Math.Max(settings.Version, AppSettings.CurrentVersion),
                UseCorrectionModel = settings.Version < AppSettings.CurrentVersion || settings.UseCorrectionModel,
                Theme = settings.Theme is "System" or "Light" or "Dark" ? settings.Theme : "System",
                Density = settings.Density is "Comfortable" or "Compact" ? settings.Density : "Comfortable",
                ResourceProfile = settings.ResourceProfile is "Auto" or "Quiet" or "Default" or "Max" ? settings.ResourceProfile : "Auto",
                SetupState = SetupPlan.Normalize(settings.SetupState),
                WatchFolder = settings.WatchFolder ?? "",
                WatchLanguage = string.IsNullOrWhiteSpace(settings.WatchLanguage) ? "auto" : settings.WatchLanguage,
                WatchOutput = settings.WatchOutput is "Text (.txt)" or "Subtitles (.srt)" or "Project only" ? settings.WatchOutput : "Text (.txt)"
            };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("Preferences could not be read: {ErrorType}", error.GetType().Name);
            LastLoadError = error.Message;
            return new();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        var temporary = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, _json));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { _gate.Release(); }
    }
}
