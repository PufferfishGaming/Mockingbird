using TriAsr.Domain;

namespace TriAsr.Infrastructure;

// The shapes the API speaks, shared by the server that writes them and the client that reads them, so that the two cannot drift apart.
// On the wire they are JSON with camelCase names; the extra fields the server adds (links, notes) are ignored by the client.

/// <summary>The open answer of <c>GET /v1/health</c>: enough for a client to decide whether to connect, without any credentials.</summary>
public sealed record RemoteHealth(string Status, string Name, string Edition, string Version, bool PasswordRequired, bool Encrypted);

/// <summary>What a connected client learns from <c>GET /v1/server</c>: whether this server can transcribe right now and how busy it is.</summary>
/// <param name="LinksEnabled">The server fetches links for the computers that use it. It does so only when it has a password.</param>
/// <param name="LiveEnabled">The server can read the phrases of live dictation right now (<c>POST /v1/live</c>): it has a speech model and the program that reads it.</param>
/// <param name="NotesEnabled">The server keeps notes for the computers that use it (<c>/v1/notes</c>).</param>
/// <param name="LinkPages">The server can also fetch the sound of web pages, not only of links straight to a file: its link helper is installed.</param>
/// <param name="Speakers">The server tells the speakers of a recording apart when asked (<c>speakers=auto</c> or how many there are).</param>
/// <param name="LanguagePairs">The server takes two languages for speech that switches between them (<c>language=en+hu</c>), for a phrase of live dictation and for a recording.</param>
/// <param name="SpeakerNames">The server keeps the names given to the speakers of a transcript (<see cref="RemoteEdits.SpeakerNames"/>).</param>
/// <param name="Search">The server searches every transcript at once (<c>GET /v1/search?q=</c>).</param>
/// <param name="Summaries">The server writes summaries of transcripts (<c>/v1/transcriptions/{id}/summary</c>): it has a language model for them.</param>
/// <param name="Phone">The server tells the address a phone on the same network opens its web page at (<c>GET /v1/phone</c>).</param>
/// <param name="Resume">The server resumes a recording whose transcription was stopped part way (<c>POST /v1/transcriptions/{id}/resume</c>).</param>
public sealed record RemoteServerInfo(string Name, string Edition, string Version, bool Encrypted, bool PasswordRequired, bool ModelsReady, string[] MissingModels, bool Busy, int Queued,
    bool LinksEnabled = false, bool LinkPages = false, bool LiveEnabled = false, bool NotesEnabled = false, bool LanguagePairs = false, bool Speakers = false, bool SpeakerNames = false, bool Search = false, bool Summaries = false, bool Phone = false, bool Resume = false);

/// <summary>Where a phone on the same network opens the web page of the server (<c>GET /v1/phone</c>), with the modules of its QR code.</summary>
/// <param name="Address">Null when the server can be reached from its own computer only.</param>
/// <param name="Rows">The QR code of the address, one string per row ('1' dark), without the light border around it; empty without an address.</param>
public sealed record RemotePhone(string? Address, string[] Rows);

/// <summary>The body of <c>POST /v1/links</c>: a web address to fetch the sound of and transcribe.</summary>
/// <param name="Speakers"><c>off</c> (or null), <c>auto</c>, or how many speakers there are.</param>
public sealed record RemoteLinkRequest(string Url, string? Language, string? Speakers = null);

/// <summary>A note as <c>GET /v1/notes/{id}</c> answers it.</summary>
public sealed record RemoteNote(Guid Id, string Title, string Text, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, int Revision);

/// <summary>A note in the list <c>GET /v1/notes</c> answers: without its text, but with the first words of it.</summary>
public sealed record RemoteNoteSummary(Guid Id, string Title, string Preview, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, int Revision, int Length);

public sealed record RemoteNotes(IReadOnlyList<RemoteNoteSummary> Data);

/// <summary>The body of <c>POST /v1/notes</c> and <c>PUT /v1/notes/{id}</c>. A save names the <paramref name="Revision"/> it was looking at; the server refuses it if the note has changed since.</summary>
public sealed record RemoteNoteEdit(string? Title, string? Text, int? Revision);

/// <param name="State"><c>queued</c>, <c>running</c>, <c>complete</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Resumable">A cancelled or failed recording that the server can run again (<c>POST /v1/transcriptions/{id}/resume</c>); an older server never says so.</param>
public sealed record RemoteJob(Guid Id, string State, string? Stage, int Percent, string Language, string Name, DateTimeOffset CreatedUtc, string? Error, bool Resumable = false)
{
    public bool IsFinished => State is "complete" or "failed" or "cancelled";
}

public sealed record RemoteLanguage(string Code, string Name, bool SecondEngine);

public sealed record RemoteLanguages(IReadOnlyList<RemoteLanguage> Data);

public sealed record RemoteJobs(IReadOnlyList<RemoteJob> Data);

/// <param name="AutomaticTexts">For each region, the text the programs produced before any manual edit (what "restore automatic result" puts back).</param>
/// <param name="RawCanaryNote">Why there is no raw Canary text, when there is none.</param>
/// <param name="SpeakerNames">The names given to the speakers ("1" -> "Anna"), or null.</param>
public sealed record RemoteReview(Guid Id, string Language, IReadOnlyList<FinalRegion> Regions, IReadOnlyList<string> AutomaticTexts, string RawWhisper, string RawCanary, string? RawCanaryNote,
    IReadOnlyDictionary<string, string>? SpeakerNames = null);

/// <summary>The answer of <c>GET /v1/search?q=</c>: the projects whose transcript or name matches, newest first, each with its first matching passages.</summary>
public sealed record RemoteSearch(string Query, IReadOnlyList<TriAsr.Application.SearchHit> Data);

/// <summary>The answer of <c>GET</c> (and <c>POST</c>) <c>/v1/transcriptions/{id}/summary</c>.</summary>
/// <param name="State"><c>none</c> (not made yet), <c>running</c>, <c>done</c> (with <paramref name="Summary"/>) or <c>failed</c> (with <paramref name="Error"/>).</param>
public sealed record RemoteSummary(string State, MeetingSummary? Summary, string? Error);

public sealed record RemoteEdit(int Index, string Text);

/// <param name="SpeakerNames">The names of the speakers as they now stand (an empty name takes the name away); null leaves them as they are.</param>
public sealed record RemoteEdits(IReadOnlyList<RemoteEdit> Edits, IReadOnlyDictionary<string, string>? SpeakerNames = null);

/// <summary>A request the server refused or could not serve. <see cref="Code"/> is the server's machine-readable code (<c>unauthorized</c>, <c>models_missing</c>...).</summary>
public sealed class RemoteException(int status, string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public bool IsAuthentication => Status == 401 || Code is "unauthorized" or "too_many_attempts";
    /// <summary>The server presented a different certificate than the one that was trusted.</summary>
    public bool IsIdentityChanged => Code == "identity_changed";
    public bool IsUnreachable => Code == "unreachable";
}
