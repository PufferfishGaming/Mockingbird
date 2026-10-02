using System.Text.Json;

namespace TriAsr.Infrastructure;

/// <summary>A server the user has connected to before: where it is, which certificate was trusted, and (if the user asked) its password, protected for this Windows account.</summary>
/// <param name="Fingerprint">The trusted certificate, or empty for a server that is reached without encryption.</param>
/// <param name="ProtectedPassword">Made by <see cref="Dpapi.Protect"/>; null when the user did not ask to remember it.</param>
public sealed record SavedServer(string Id, string Name, string Address, string Fingerprint, string? ProtectedPassword);

/// <summary>The servers this computer knows, kept in a small file in the data folder.</summary>
public sealed class SavedServerStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<SavedServer> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<SavedServer>>(File.ReadAllText(path), Json)?
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && Uri.TryCreate(item.Address, UriKind.Absolute, out _)).ToArray() ?? [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public void Save(IEnumerable<SavedServer> servers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(servers.ToArray(), Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
