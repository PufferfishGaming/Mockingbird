using TriAsr.Alignment;
using TriAsr.Domain;

namespace TriAsr.Fusion;

public sealed record Disagreement(int Segment, int StartCharacter, int Length, string Whisper, string Canary, string Before, string After);
public sealed record ComparisonResult(IReadOnlyList<FinalRegion> Regions, IReadOnlyList<Disagreement> Disagreements, AlignmentResult Alignment);

public static class DisagreementDetector
{
    public static ComparisonResult Compare(EngineTranscript whisper, EngineTranscript canary)
    {
        var alignment = TokenAligner.Align(whisper.Segments, canary.Text, whisper.Language);
        var projected = Enumerable.Range(0, whisper.Segments.Count).Select(_ => new List<int>()).ToArray();
        var segment = 0;
        var owners = new int[alignment.Operations.Count];
        var anchors = new int[alignment.Operations.Count];
        var anchor = 0;
        var operationIndex = 0;
        foreach (var operation in alignment.Operations)
        {
            if (operation.WhisperIndex is { } index)
            {
                var leftToken = alignment.WhisperTokens[index];
                segment = leftToken.Segment; anchor = leftToken.Start;
            }
            owners[operationIndex] = segment; anchors[operationIndex++] = anchor;
            if (operation.CanaryIndex is { } right && projected.Length > 0) projected[segment].Add(right);
            if (operation.WhisperIndex is { } left) anchor = alignment.WhisperTokens[left].Start + alignment.WhisperTokens[left].Length;
        }
        var regions = whisper.Segments.Select((item, index) =>
        {
            var indices = projected[index];
            var text = indices.Count == 0 ? "" : CanarySlice(canary.Text, alignment.CanaryTokens, indices.Min(), indices.Max());
            return new FinalRegion(item.StartMs, item.EndMs, item.Text, item.Text, text, "agreement", Speaker: item.Speaker);
        }).ToArray();
        var disagreements = new List<Disagreement>();
        for (var cursor = 0; cursor < alignment.Operations.Count;)
        {
            if (alignment.Operations[cursor].Operation == AlignmentOperation.Match) { cursor++; continue; }
            var begin = cursor;
            var ownerSegment = owners[cursor];
            var group = new List<AlignedToken>();
            while (cursor < alignment.Operations.Count && alignment.Operations[cursor].Operation != AlignmentOperation.Match && owners[cursor] == ownerSegment) group.Add(alignment.Operations[cursor++]);
            var leftIndices = group.Where(item => item.WhisperIndex.HasValue).Select(item => item.WhisperIndex!.Value).ToArray();
            var rightIndices = group.Where(item => item.CanaryIndex.HasValue).Select(item => item.CanaryIndex!.Value).ToArray();
            if (regions.Length == 0) break;
            {
                var left = leftIndices;
                var source = whisper.Segments[ownerSegment].Text;
                var start = left.Length > 0 ? alignment.WhisperTokens[left[0]].Start : Math.Min(source.Length, anchors[begin]);
                var end = left.Length > 0 ? alignment.WhisperTokens[left[^1]].Start + alignment.WhisperTokens[left[^1]].Length : start;
                var a = source[start..end];
                var b = rightIndices.Length == 0 ? "" : CanarySlice(canary.Text, alignment.CanaryTokens, rightIndices[0], rightIndices[^1]);
                if (TokenAligner.Normalize(a, whisper.Language) == TokenAligner.Normalize(b, whisper.Language)) continue;
                disagreements.Add(new(ownerSegment, start, end - start, a, b,
                    source[Math.Max(0, start - 160)..start], source[end..Math.Min(source.Length, end + 160)]));
                regions[ownerSegment] = regions[ownerSegment] with { Source = "uncertain" };
            }
        }
        return new(regions, disagreements, alignment);
    }
    private static string CanarySlice(string text, IReadOnlyList<ComparisonToken> tokens, int first, int last) =>
        text[tokens[first].Start..(tokens[last].Start + tokens[last].Length)];
}
