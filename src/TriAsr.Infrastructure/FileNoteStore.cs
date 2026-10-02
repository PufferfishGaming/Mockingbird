using System.Text.Json;
using TriAsr.Application;

namespace TriAsr.Infrastructure;

/// <summary>
/// Keeps each note as a JSON file of its own in the <c>Notes</c> folder of the data folder (<c>&lt;id&gt;.json</c>), so that a note can be found, copied and deleted without the
/// program. The notes are read once and kept in memory; every change is written to its file at once (to a temporary file first and moved over the old one, so that
/// a crash never leaves half a note). A file that cannot be read is left where it is and skipped.
/// </summary>
public sealed class FileNoteStore(string root) : INoteStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, UserNote>? _notes;

    public string Folder => Path.Combine(root, "Notes");

    private string PathOf(Guid id) => Path.Combine(Folder, id.ToString("N") + ".json");

    private Dictionary<Guid, UserNote> Load()
    {
        if (_notes is not null) return _notes;
        var notes = new Dictionary<Guid, UserNote>();
        if (Directory.Exists(Folder))
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var note = JsonSerializer.Deserialize<UserNote>(stream, Json);
                    if (note is null || note.Id == Guid.Empty || Path.GetFileNameWithoutExtension(file) != note.Id.ToString("N")) continue;
                    notes[note.Id] = note with { Title = note.Title ?? "", Text = note.Text ?? "", Revision = Math.Max(1, note.Revision) };
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
        return _notes = notes;
    }

    private static void Check(string title, string text)
    {
        if (title.Length > NoteMessages.MaxTitle || text.Length > NoteMessages.MaxText) throw new NoteException(NoteMessages.TooLong);
    }

    private void Write(UserNote note)
    {
        Directory.CreateDirectory(Folder);
        var path = PathOf(note.Id);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(note, Json));
        File.Move(temporary, path, true);
    }

    public async Task<IReadOnlyList<UserNote>> ListAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return Load().Values.OrderByDescending(note => note.UpdatedUtc).ThenByDescending(note => note.CreatedUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<UserNote?> GetAsync(Guid id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return Load().GetValueOrDefault(id); }
        finally { _gate.Release(); }
    }

    public async Task<UserNote> CreateAsync(string title, string text, CancellationToken token)
    {
        title = title.Trim();
        Check(title, text);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var note = new UserNote(Guid.NewGuid(), title, text, now, now, 1);
            Write(note);
            Load()[note.Id] = note;
            return note;
        }
        finally { _gate.Release(); }
    }

    public async Task<UserNote> SaveAsync(Guid id, string title, string text, int? expectedRevision, CancellationToken token)
    {
        title = title.Trim();
        Check(title, text);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Load().TryGetValue(id, out var current)) throw new NoteException(NoteMessages.NotFound);
            if (expectedRevision is { } expected && expected != current.Revision) throw new NoteException(NoteMessages.Changed);
            if (current.Title == title && current.Text == text) return current;                  // nothing changed: nothing is written and the revision stays
            var saved = current with { Title = title, Text = text, UpdatedUtc = DateTimeOffset.UtcNow, Revision = current.Revision + 1 };
            Write(saved);
            Load()[id] = saved;
            return saved;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Load().Remove(id)) return false;
            try { File.Delete(PathOf(id)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _notes = null;                             // the file is still there, so it must not look deleted: the folder is read again next time
                throw;
            }
            return true;
        }
        finally { _gate.Release(); }
    }
}
