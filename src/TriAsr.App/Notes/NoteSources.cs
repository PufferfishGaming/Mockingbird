using System.IO;
using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>A note as the list shows it.</summary>
public sealed record NoteInfo(Guid Id, string Title, string Preview, DateTimeOffset UpdatedUtc, int Revision);

/// <summary>A note with its text, as the editor opens it.</summary>
public sealed record NoteData(Guid Id, string Title, string Text, DateTimeOffset UpdatedUtc, int Revision);

public enum NoteFailure
{
    /// <summary>Someone saved the note after this window opened it.</summary>
    Changed,
    /// <summary>The note is gone.</summary>
    Missing,
    /// <summary>The server cannot be reached or does not let this window in any more.</summary>
    Unreachable,
    /// <summary>The note is too long.</summary>
    TooLong,
    Other
}

/// <summary>A note could not be read or saved. The message is in English and goes through <see cref="Loc.Describe"/> before it is shown.</summary>
public sealed class NoteSourceException(NoteFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public NoteFailure Failure { get; } = failure;
}

/// <summary>Where the notes page gets its notes from: the files of this computer, or the server this window is connected to.</summary>
public interface INoteSource
{
    Task<IReadOnlyList<NoteInfo>> ListAsync(CancellationToken token);
    Task<NoteData> GetAsync(Guid id, CancellationToken token);
    Task<NoteData> CreateAsync(string title, string text, CancellationToken token);

    /// <param name="revision">The revision this window opened; a note that was saved since is refused (<see cref="NoteFailure.Changed"/>).</param>
    Task<NoteData> SaveAsync(Guid id, string title, string text, int revision, CancellationToken token);
    Task DeleteAsync(Guid id, CancellationToken token);
}

/// <summary>The notes kept on this computer.</summary>
public sealed class LocalNoteSource(INoteStore store) : INoteSource
{
    private static NoteData Data(UserNote note) => new(note.Id, note.Title, note.Text, note.UpdatedUtc, note.Revision);

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (NoteException error) { throw new NoteSourceException(error.Message switch { NoteMessages.NotFound => NoteFailure.Missing, NoteMessages.Changed => NoteFailure.Changed, NoteMessages.TooLong => NoteFailure.TooLong, _ => NoteFailure.Other }, error.Message, error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new NoteSourceException(NoteFailure.Other, error.Message, error); }
    }

    public async Task<IReadOnlyList<NoteInfo>> ListAsync(CancellationToken token) =>
        (await GuardAsync(async () => await store.ListAsync(token))).Select(note => new NoteInfo(note.Id, note.Title, NoteText.Preview(note.Text), note.UpdatedUtc, note.Revision)).ToArray();

    public Task<NoteData> GetAsync(Guid id, CancellationToken token) =>
        GuardAsync(async () => Data(await store.GetAsync(id, token) ?? throw new NoteException(NoteMessages.NotFound)));

    public Task<NoteData> CreateAsync(string title, string text, CancellationToken token) =>
        GuardAsync(async () => Data(await store.CreateAsync(title, text, token)));

    public Task<NoteData> SaveAsync(Guid id, string title, string text, int revision, CancellationToken token) =>
        GuardAsync(async () => Data(await store.SaveAsync(id, title, text, revision, token)));

    public Task DeleteAsync(Guid id, CancellationToken token) =>
        GuardAsync(async () => await store.DeleteAsync(id, token) ? true : throw new NoteException(NoteMessages.NotFound));
}

/// <summary>The notes kept on the server this window is connected to.</summary>
public sealed class RemoteNoteSource(Func<RemoteServerClient?> client) : INoteSource
{
    private RemoteServerClient Client() =>
        client() ?? throw new NoteSourceException(NoteFailure.Unreachable, Loc.Key("Connect to a server to use its notes."));

    private static NoteData Data(RemoteNote note) => new(note.Id, note.Title, note.Text, note.UpdatedUtc, note.Revision);

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (RemoteException error)
        {
            var failure = error.Code switch
            {
                "note_changed" => NoteFailure.Changed,
                "not_found" => NoteFailure.Missing,
                "payload_too_large" => NoteFailure.TooLong,
                "notes_unavailable" => NoteFailure.Other,
                _ => error.IsUnreachable || error.IsAuthentication || error.IsIdentityChanged ? NoteFailure.Unreachable : NoteFailure.Other
            };
            throw new NoteSourceException(failure, error.Message, error);
        }
    }

    public async Task<IReadOnlyList<NoteInfo>> ListAsync(CancellationToken token) =>
        (await GuardAsync(async () => await Client().NotesAsync(token))).Select(note => new NoteInfo(note.Id, note.Title, note.Preview, note.UpdatedUtc, note.Revision)).ToArray();

    public Task<NoteData> GetAsync(Guid id, CancellationToken token) => GuardAsync(async () => Data(await Client().NoteAsync(id, token)));

    public Task<NoteData> CreateAsync(string title, string text, CancellationToken token) => GuardAsync(async () => Data(await Client().CreateNoteAsync(title, text, token)));

    public Task<NoteData> SaveAsync(Guid id, string title, string text, int revision, CancellationToken token) =>
        GuardAsync(async () => Data(await Client().SaveNoteAsync(id, title, text, revision, token)));

    public Task DeleteAsync(Guid id, CancellationToken token) => GuardAsync(async () => { await Client().DeleteNoteAsync(id, token); return true; });
}
