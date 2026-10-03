using TriAsr.Alignment;
using TriAsr.Domain;

namespace TriAsr.Alignment.Tests;

public sealed class NumberComparisonTests
{
    [Theory]
    [InlineData("en", "twenty five", "25")]
    [InlineData("en", "ninety-nine", "99")]
    [InlineData("de", "fünfundzwanzig", "25")]
    [InlineData("de", "einundzwanzig", "21")]
    [InlineData("hu", "huszonöt", "25")]
    [InlineData("hu", "tizenkettő", "12")]
    [InlineData("en+hu", "twenty five", "25")]                                                       // a recording in two languages: the number words of both count
    [InlineData("en+hu", "huszonöt", "25")]
    [InlineData("hu+de", "einundzwanzig", "21")]
    public void WrittenNumbersAlignWithoutChangingTheDisplayedEvidence(string language, string written, string digits)
    {
        var result = TokenAligner.Align([new TranscriptSegment(0, 1000, written)], digits, language);
        Assert.All(result.Operations, operation => Assert.Equal(AlignmentOperation.Match, operation.Operation));
        Assert.Equal(written, Assert.Single(result.WhisperTokens).Original);
    }
    [Theory]
    [InlineData("3.14", "314")]
    [InlineData("9:30", "930")]
    [InlineData("-1", "1")]
    [InlineData("25%", "25")]
    [InlineData("1/4", "14")]
    [InlineData("20-25", "2025")]
    public void MeaningfulNumericPunctuationDoesNotBecomeFalseAgreement(string first, string second)
        => Assert.NotEqual(TokenAligner.Normalize(first, "en"), TokenAligner.Normalize(second, "en"));
    [Fact]
    public void SeparateNumbersAndRepetitionsAreNotMerged()
    {
        Assert.Equal(2, TokenAligner.Tokenize("twenty, five", "en").Length);
        Assert.Equal(2, TokenAligner.Tokenize("one one", "en").Length);
        Assert.Equal(2, TokenAligner.Tokenize("twenty, five", "en+hu").Length);
        Assert.Single(TokenAligner.Tokenize("twenty five", "hu+en"));                              // English number pairs are joined when English is one of the two
        Assert.Equal(2, TokenAligner.Tokenize("twenty five", "hu+de").Length);
    }
}
