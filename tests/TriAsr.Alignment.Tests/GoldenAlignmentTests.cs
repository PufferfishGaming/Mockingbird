using TriAsr.Alignment;
using TriAsr.Domain;

namespace TriAsr.Alignment.Tests;

public sealed class GoldenAlignmentTests
{
    [Fact]
    public void SegmentationDoesNotChangeLexicalAlignment()
    {
        TranscriptSegment[] segments = [new(0, 1000, "Ich habe gesehen,"), new(1000, 2000, "dass ein Verein unsere Schulternhalle benutzt.")];
        var aligned = TokenAligner.Align(segments, "Ich habe gesehen dass ein Verein unsere Schulturnhalle benutzt", "de");
        var disagreement = Assert.Single(aligned.Operations, operation => operation.Operation != AlignmentOperation.Match);
        Assert.Equal(AlignmentOperation.Substitute, disagreement.Operation);
        Assert.Equal("Schulternhalle", aligned.WhisperTokens[disagreement.WhisperIndex!.Value].Original);
    }
    [Fact]
    public void ComparisonPreservesHungarianAccentsAndNormalizesNumbers()
    {
        Assert.NotEqual(TokenAligner.Normalize("kor", "hu"), TokenAligner.Normalize("kór", "hu"));
        Assert.Equal(TokenAligner.Normalize("neunzig", "de"), TokenAligner.Normalize("90", "de"));
        var aligned = TokenAligner.Align([new(0, 1000, "Igen, igen!")], "IGEN igen igen", "hu");
        Assert.Single(aligned.Operations, operation => operation.Operation == AlignmentOperation.Insert);
    }
    [Fact]
    public void LinearMemoryPathRetainsEveryTokenExactlyOnce()
    {
        var first = string.Join(" ", Enumerable.Range(0, 180).Select(i => "word" + i));
        var second = first.Replace("word90", "changed", StringComparison.Ordinal) + " appended";
        var result = TokenAligner.Align([new(0, 1000, first)], second, "en");
        Assert.Equal(180, result.Operations.Count(op => op.WhisperIndex.HasValue));
        Assert.Equal(181, result.Operations.Count(op => op.CanaryIndex.HasValue));
        Assert.Single(result.Operations, op => op.Operation == AlignmentOperation.Substitute);
        Assert.Single(result.Operations, op => op.Operation == AlignmentOperation.Insert);
    }
}
