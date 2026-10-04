using System.Globalization;
using TriAsr.Domain;

namespace TriAsr.Application;

/// <summary>A passage of a transcript that matches a search: the region, and its text cut around the match.</summary>
/// <param name="Index">The region in the transcript, to open it there.</param>
/// <param name="Speaker">The name given to the speaker, or the speaker's number; null when the speakers were not told apart.</param>
/// <param name="Before">The text before the match (shortened from the left, with "…").</param>
/// <param name="Match">The matching text as the transcript writes it.</param>
/// <param name="After">The text after the match (shortened from the right, with "…").</param>
public sealed record SearchPassage(int Index, long StartMs, long EndMs, bool NativeTimestamps, string? Speaker, bool SpeakerNamed, string Before, string Match, string After);

/// <summary>A project whose name or transcript matches a search.</summary>
/// <param name="Matches">How many regions match (the passages show the first few).</param>
public sealed record SearchHit(Guid JobId, string Name, DateTimeOffset CreatedUtc, bool NameMatches, int Matches, IReadOnlyList<SearchPassage> Passages);

/// <summary>A project to search: what the person sees of it, and how to read its transcript (null when it has none yet).</summary>
public sealed record SearchableProject(Guid JobId, string Name, DateTimeOffset CreatedUtc, bool HasTranscript);

/// <summary>
/// Searching every transcript at once (Studio's Projects page, a server's <c>GET /v1/search</c>). Case and accents do not matter, so
/// "kotelezo" finds "kötelező" and "Ubung" finds "Übung"; the query is looked for as it is written, words in that order.
/// </summary>
public static class ProjectSearch
{
    public const int ShortestQuery = 2;
    public const int PassagesPerProject = 5;
    public const int ProjectsAtMost = 100;
    private const int Context = 60;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    /// <summary>The query as it is searched for: trimmed, inner spaces made single; empty when it is too short to search.</summary>
    public static string Normalize(string? query)
    {
        var text = string.Join(' ', (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length >= ShortestQuery ? text : "";
    }

    /// <summary>Where the query is in a text, ignoring case, accents and extra spaces ("zur  Sitzung" is found by "zur Sitzung"); null when it is not.</summary>
    public static (int Start, int Length)? Find(string text, string query)
    {
        if (query.Length == 0 || text.Length == 0) return null;
        // The text with every run of white space as one space, and where each of its characters came from, so that the match is cut from the text itself.
        var collapsed = new System.Text.StringBuilder(text.Length);
        var from = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (collapsed.Length > 0 && collapsed[^1] == ' ') continue;
                collapsed.Append(' ');
            }
            else collapsed.Append(text[i]);
            from.Add(i);
        }
        var start = CultureInfo.InvariantCulture.CompareInfo.IndexOf(collapsed.ToString().AsSpan(), query.AsSpan(), Loose, out var length);
        if (start < 0) return null;
        var first = from[start];
        var last = from[start + length - 1];
        return (first, last - first + 1);
    }

    /// <summary>The matches in one transcript, or null when neither its name nor its text matches.</summary>
    public static SearchHit? Search(SearchableProject project, FinalTranscript? transcript, string query)
    {
        query = Normalize(query);
        if (query.Length == 0) return null;
        var nameMatches = Find(project.Name, query) is not null;
        var passages = new List<SearchPassage>();
        var matches = 0;
        if (transcript is not null)
            for (var index = 0; index < transcript.Regions.Count; index++)
            {
                var region = transcript.Regions[index];
                if (Find(region.FinalText, query) is not { } found) continue;
                matches++;
                if (passages.Count < PassagesPerProject) passages.Add(Cut(transcript, index, found));
            }
        return nameMatches || matches > 0 ? new SearchHit(project.JobId, project.Name, project.CreatedUtc, nameMatches, matches, passages) : null;
    }

    /// <summary>Searches the projects, newest first, reading each transcript when it is needed; at most <see cref="ProjectsAtMost"/> projects are returned.</summary>
    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(IEnumerable<SearchableProject> projects, Func<Guid, CancellationToken, Task<FinalTranscript?>> read,
        string query, CancellationToken token)
    {
        query = Normalize(query);
        if (query.Length == 0) return [];
        var hits = new List<SearchHit>();
        foreach (var project in projects.OrderByDescending(project => project.CreatedUtc))
        {
            token.ThrowIfCancellationRequested();
            FinalTranscript? transcript = null;
            if (project.HasTranscript)
                try { transcript = await read(project.JobId, token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }   // a project whose files are gone or broken is skipped, not the search
            if (Search(project, transcript, query) is { } hit) hits.Add(hit);
            if (hits.Count >= ProjectsAtMost) break;
        }
        return hits;
    }

    private static SearchPassage Cut(FinalTranscript transcript, int index, (int Start, int Length) found)
    {
        var region = transcript.Regions[index];
        var text = region.FinalText;
        var before = text[..found.Start];
        var after = text[(found.Start + found.Length)..];
        if (before.Length > Context) before = "…" + TrimToWord(before[^Context..], fromStart: true);
        if (after.Length > Context) after = TrimToWord(after[..Context], fromStart: false) + "…";
        var name = transcript.NameOf(region.Speaker);
        return new SearchPassage(index, region.StartMs, region.EndMs, region.NativeTimestamps, name ?? region.Speaker, name is not null,
            before.TrimStart(), text.Substring(found.Start, found.Length), after.TrimEnd());
    }

    /// <summary>Does not cut a word in half at the edge of a shortened passage.</summary>
    private static string TrimToWord(string text, bool fromStart)
    {
        if (fromStart) { var space = text.IndexOf(' '); return space is > 0 and < 20 ? text[(space + 1)..] : text; }
        var last = text.LastIndexOf(' ');
        return last > text.Length - 20 && last > 0 ? text[..last] : text;
    }
}
