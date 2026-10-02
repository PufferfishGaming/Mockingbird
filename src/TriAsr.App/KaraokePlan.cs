namespace TriAsr.App;

/// <summary>
/// When each word of a region is said, as a fraction of the region's time (ADR-0017). The transcript keeps the time of each region, not of each word,
/// so the time is shared out over the words by their length, with a longer wait after a sentence than after a comma. That is close enough to follow the
/// words by ear, it works for any text (the programs' own, a correction, the user's edit), and exact word times can replace the weights later.
/// The web page (<c>Web/app.js</c>) uses the same weights; a test keeps the two descriptions the same.
/// </summary>
public static class KaraokePlan
{
    /// <summary>Weight of the pause after a word that ends a sentence, and after one that ends a clause, in letters.</summary>
    public const double SentencePause = 4, ClausePause = 2;

    /// <param name="Start">Where the word begins in the text.</param>
    /// <param name="Length">Its length, with the punctuation that sticks to it.</param>
    /// <param name="From">The fraction of the region's time (0 to 1) at which it begins.</param>
    /// <param name="To">The fraction at which the next word begins (1 for the last).</param>
    public readonly record struct WordSpan(int Start, int Length, double From, double To);

    public static IReadOnlyList<WordSpan> Words(string text)
    {
        var found = new List<(int Start, int Length, double Weight)>();
        for (var index = 0; index < text.Length;)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
            if (index == start) break;
            var word = text.AsSpan(start, index - start);
            var letters = 0;
            foreach (var character in word) if (char.IsLetterOrDigit(character)) letters++;
            var weight = Math.Max(1, letters) + word[^1] switch
            {
                '.' or '!' or '?' or '…' => SentencePause,
                ',' or ';' or ':' or '–' or '—' => ClausePause,
                _ => 0
            };
            found.Add((start, index - start, weight));
        }
        var total = found.Sum(item => item.Weight);
        var spans = new List<WordSpan>(found.Count);
        var at = 0.0;
        foreach (var (start, length, weight) in found)
        {
            var to = at + weight / total;
            spans.Add(new WordSpan(start, length, at, to));
            at = to;
        }
        if (spans.Count > 0) spans[^1] = spans[^1] with { To = 1 };
        return spans;
    }

    /// <summary>The index of the word being said at <paramref name="progress"/> (0 to 1) of the region, or -1 when there are no words.</summary>
    public static int WordAt(IReadOnlyList<WordSpan> words, double progress)
    {
        if (words.Count == 0) return -1;
        for (var index = 0; index < words.Count; index++) if (progress < words[index].To) return index;
        return words.Count - 1;
    }
}
