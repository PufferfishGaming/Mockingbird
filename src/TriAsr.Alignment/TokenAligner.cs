using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TriAsr.Domain;

namespace TriAsr.Alignment;

public enum AlignmentOperation { Match, Substitute, Insert, Delete }
public sealed record AlignedToken(AlignmentOperation Operation, int? WhisperIndex, int? CanaryIndex);
public sealed record ComparisonToken(string Original, string Normalized, int Start, int Length, int Segment);
public sealed record AlignmentResult(IReadOnlyList<ComparisonToken> WhisperTokens, IReadOnlyList<ComparisonToken> CanaryTokens, IReadOnlyList<AlignedToken> Operations);

public static partial class TokenAligner
{
    public static AlignmentResult Align(IReadOnlyList<TranscriptSegment> whisper, string canary, string language)
    {
        var left = new List<ComparisonToken>();
        for (var segment = 0; segment < whisper.Count; segment++) left.AddRange(Tokenize(whisper[segment].Text, language, segment));
        var right = Tokenize(canary, language, -1);
        var operations = new List<AlignedToken>();
        Hirschberg(left, 0, left.Count, right, 0, right.Length, operations);
        return new(left, right, operations);
    }
    public static ComparisonToken[] Tokenize(string text, string language, int segment = -1)
    {
        var tokens = Words().Matches(text).Select(match => new ComparisonToken(match.Value, Normalize(match.Value, language), match.Index, match.Length, segment)).Where(token => token.Normalized.Length > 0).ToArray();
        if (!language.Split('+').Contains("en")) return tokens;
        var merged = new List<ComparisonToken>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var current = tokens[i];
            if (i + 1 < tokens.Length && NumberComparison.TryEnglishPair(current.Original, tokens[i + 1].Original, out var number))
            {
                var next = tokens[++i];
                var length = next.Start + next.Length - current.Start;
                merged.Add(current with { Original = text.Substring(current.Start, length), Length = length, Normalized = number });
            }
            else merged.Add(current);
        }
        return merged.ToArray();
    }
    public static string Normalize(string value, string language)
    {
        var canonical = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var numeric = canonical.Trim().Trim('(', ')', '[', ']', '.', ',', '!', '?', ';').Replace('−', '-').Replace('–', '-');
        if (Regex.IsMatch(numeric, @"^[+-]?\d+(?:[.,:/-]\d+)*%?$", RegexOptions.CultureInvariant)) return numeric;
        var normalized = new string(canonical.Where(character => char.IsLetterOrDigit(character)).ToArray());
        return NumberComparison.Normalize(normalized, language);
    }
    private static void Hirschberg(IReadOnlyList<ComparisonToken> a, int ai, int an, IReadOnlyList<ComparisonToken> b, int bi, int bn, List<AlignedToken> output)
    {
        if (an == 0) { for (var j = 0; j < bn; j++) output.Add(new(AlignmentOperation.Insert, null, bi + j)); return; }
        if (bn == 0) { for (var i = 0; i < an; i++) output.Add(new(AlignmentOperation.Delete, ai + i, null)); return; }
        if (an <= 32 || bn <= 32) { SmallAlign(a, ai, an, b, bi, bn, output); return; }
        var half = an / 2;
        var forward = Row(a, ai, half, b, bi, bn, false);
        var backward = Row(a, ai + half, an - half, b, bi, bn, true);
        var split = 0; var best = int.MaxValue;
        for (var j = 0; j <= bn; j++) if (forward[j] + backward[bn - j] < best) { best = forward[j] + backward[bn - j]; split = j; }
        Hirschberg(a, ai, half, b, bi, split, output);
        Hirschberg(a, ai + half, an - half, b, bi + split, bn - split, output);
    }
    private static int[] Row(IReadOnlyList<ComparisonToken> a, int ai, int an, IReadOnlyList<ComparisonToken> b, int bi, int bn, bool reverse)
    {
        var previous = Enumerable.Range(0, bn + 1).ToArray();
        for (var i = 1; i <= an; i++)
        {
            var current = new int[bn + 1]; current[0] = i;
            for (var j = 1; j <= bn; j++)
            {
                var equal = a[ai + (reverse ? an - i : i - 1)].Normalized == b[bi + (reverse ? bn - j : j - 1)].Normalized;
                current[j] = Math.Min(previous[j - 1] + (equal ? 0 : 1), Math.Min(previous[j] + 1, current[j - 1] + 1));
            }
            previous = current;
        }
        return previous;
    }
    private static void SmallAlign(IReadOnlyList<ComparisonToken> a, int ai, int an, IReadOnlyList<ComparisonToken> b, int bi, int bn, List<AlignedToken> output)
    {
        var costs = new int[an + 1, bn + 1];
        for (var i = 0; i <= an; i++) costs[i, 0] = i;
        for (var j = 0; j <= bn; j++) costs[0, j] = j;
        for (var i = 1; i <= an; i++) for (var j = 1; j <= bn; j++)
            costs[i, j] = Math.Min(costs[i - 1, j - 1] + (a[ai + i - 1].Normalized == b[bi + j - 1].Normalized ? 0 : 1), Math.Min(costs[i - 1, j] + 1, costs[i, j - 1] + 1));
        var reversed = new List<AlignedToken>();
        var x = an; var y = bn;
        while (x > 0 || y > 0)
        {
            var equal = x > 0 && y > 0 && a[ai + x - 1].Normalized == b[bi + y - 1].Normalized;
            if (x > 0 && y > 0 && costs[x, y] == costs[x - 1, y - 1] + (equal ? 0 : 1)) { reversed.Add(new(equal ? AlignmentOperation.Match : AlignmentOperation.Substitute, ai + --x, bi + --y)); }
            else if (x > 0 && costs[x, y] == costs[x - 1, y] + 1) reversed.Add(new(AlignmentOperation.Delete, ai + --x, null));
            else reversed.Add(new(AlignmentOperation.Insert, null, bi + --y));
        }
        reversed.Reverse(); output.AddRange(reversed);
    }
    [GeneratedRegex(@"\S+")] private static partial Regex Words();
}
