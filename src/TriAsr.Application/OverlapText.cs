namespace TriAsr.Application;

/// <summary>
/// Chunks that were cut inside continuous speech overlap by about a second, so the same few words come out at the end of one
/// chunk and the start of the next. The words repeated at the start are removed; a plain repeat of a word is only removed at
/// such a seam, never inside a chunk.
/// </summary>
public static class OverlapText
{
    public const int MaximumWords = 6;

    public static string TrimRepeatedStart(string previous, string current, int maximumWords = MaximumWords)
    {
        var before = Words(previous); var after = Words(current);
        for (var count = Math.Min(maximumWords, Math.Min(before.Length, after.Length)); count >= 1; count--)
        {
            var same = true;
            for (var i = 0; i < count && same; i++)
            {
                var key = Key(after[i]);
                same = key.Length > 0 && key == Key(before[before.Length - count + i]);
            }
            if (same) return string.Join(' ', after.Skip(count));
        }
        return current;
    }

    /// <summary>Joins chunk texts in order, trimming the repeated start of every chunk that overlaps the one before it.</summary>
    public static string Join(IEnumerable<(string Text, bool OverlapsPrevious)> chunks)
    {
        var parts = new List<string>(); var previous = "";
        foreach (var (text, overlaps) in chunks)
        {
            var kept = overlaps && previous.Length > 0 ? TrimRepeatedStart(previous, text) : text.Trim();
            if (kept.Length > 0) parts.Add(kept);
            if (text.Trim().Length > 0) previous = text;
        }
        return string.Join(' ', parts);
    }

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static string Key(string word) => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
