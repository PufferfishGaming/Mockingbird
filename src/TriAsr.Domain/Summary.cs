namespace TriAsr.Domain;

/// <summary>A task someone took on in a conversation.</summary>
/// <param name="When">When it is due, if a time was said; empty otherwise.</param>
public sealed record SummaryAction(string Who, string What, string When = "");

/// <summary>
/// What a conversation was about, as the local language model wrote it from the transcript (ADR: summaries). It is a draft to check against the
/// transcript, not part of the transcript: it is kept beside it (summary.json in the job's folder) and made again on request.
/// </summary>
/// <param name="Language">The language it is written in (the transcript's), as a code.</param>
/// <param name="Model">The model file that wrote it.</param>
public sealed record MeetingSummary(string Summary, IReadOnlyList<string> KeyPoints, IReadOnlyList<string> Decisions, IReadOnlyList<SummaryAction> ActionItems,
    IReadOnlyList<string> OpenQuestions, string Language, string Model, DateTimeOffset CreatedUtc);
