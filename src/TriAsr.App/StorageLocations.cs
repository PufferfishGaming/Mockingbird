using System.IO;
using System.Text.Json;

namespace TriAsr.App;

public static class StorageLocations
{
    public sealed record Location(string? DataRoot = null, string? ModelRoot = null);
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.DataFolderName, "storage-location.json");
    public static string? LastLoadError { get; private set; }
    public static Location Load()
    {
        LastLoadError = null;
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Location>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { LastLoadError = error.Message; return new(); }
    }
    public static void Save(Location location)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(location)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
