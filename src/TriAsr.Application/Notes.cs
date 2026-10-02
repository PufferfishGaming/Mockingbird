namespace TriAsr.Application;

/// <summary>A note the person wrote or dictated (ADR: notes). It is kept as a file of its own on the computer that holds it: this one in Studio, the server's for a Client and for the web page.</summary>
/// <param name="Title">What the person called it; empty when they did not. The list then shows the first words of the text.</param>
/// <param name="Revision">1 for a new note, and one more each time it is saved. A save that names an older revision is refused, so that two windows never overwrite each other's words.</param>
public sealed record UserNote(Guid Id, string Title, string Text, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, int Revision);

/// <summary>Where the notes are kept.</summary>
public interface INoteStore
{
    /// <summary>Every note, the one changed last first.</summary>
    Task<IReadOnlyList<UserNote>> ListAsync(CancellationToken token);

    Task<UserNote?> GetAsync(Guid id, CancellationToken token);

    /// <exception cref="NoteException">The title or the text is too long.</exception>
    Task<UserNote> CreateAsync(string title, string text, CancellationToken token);

    /// <param name="expectedRevision">The revision the person was looking at; null saves whatever is there.</param>
    /// <exception cref="NoteException">The note is gone (<see cref="NoteMessages.NotFound"/>), was saved by someone else meanwhile (<see cref="NoteMessages.Changed"/>) or is too long.</exception>
    Task<UserNote> SaveAsync(Guid id, string title, string text, int? expectedRevision, CancellationToken token);

    /// <returns>False when there was no such note.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken token);
}

/// <summary>A note could not be saved. The message is one of <see cref="NoteMessages"/> (English, translated by the app).</summary>
public sealed class NoteException(string message) : Exception(message);

/// <summary>The sentences the notes say to a person from the layers below the interface; each is listed in <c>extra-keys.json</c> and translated by the app.</summary>
public static class NoteMessages
{
    public const string NotFound = "This note no longer exists.";
    public const string Changed = "This note was changed on another computer.";
    public const string TooLong = "The note is too long.";

    public static readonly IReadOnlyList<string> All = [NotFound, Changed, TooLong];

    public const int MaxTitle = 200;

    /// <summary>About 500 pages: more than anyone dictates, and few enough for a note to stay quick to open and to send.</summary>
    public const int MaxText = 1_000_000;
}

public static class NoteText
{
    /// <summary>The first words of a note for a list: line breaks and runs of spaces become one space, and a note that goes on ends in "…".</summary>
    public static string Preview(string text, int length = 160)
    {
        var builder = new System.Text.StringBuilder(length + 1);
        var space = false;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (char.IsWhiteSpace(character)) { space = builder.Length > 0; continue; }
            if (space) { builder.Append(' '); space = false; }
            builder.Append(character);
            if (builder.Length >= length) return builder.ToString().TrimEnd() + (text.AsSpan(i + 1).Trim().Length > 0 ? "…" : "");
        }
        return builder.ToString();
    }
}