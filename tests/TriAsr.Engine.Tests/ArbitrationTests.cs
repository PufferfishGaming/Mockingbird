using TriAsr.Engine.Llm;

namespace TriAsr.Engine.Tests;

public sealed class ArbitrationTests
{
    [Fact]
    public void OneSidedHallucinationMustRemainUncertainEvenWithHighModelConfidence()
    {
        var result = ArbitrationValidation.Parse("{\"choice\":\"whisper\",\"text\":\"Subtitles 2020\",\"confidence\":0.99,\"uncertain\":false}", "Subtitles 2020", "");
        Assert.True(result.Uncertain);
    }
    [Fact]
    public void UnsupportedWordingAndInvalidChoicesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ArbitrationValidation.Parse("""{"choice":"merged","text":"a new invention","confidence":0.9,"uncertain":false}""", "a cat", "a dog"));
        Assert.Throws<InvalidDataException>(() => ArbitrationValidation.Parse("""{"choice":"cloud","text":"a cat","confidence":0.9,"uncertain":false}""", "a cat", "a dog"));
        var accepted = ArbitrationValidation.Parse("""{"choice":"canary","text":"Schulturnhalle","confidence":0.94,"uncertain":false}""", "Schulternhalle", "Schulturnhalle");
        Assert.False(accepted.Uncertain); Assert.Equal("canary", accepted.Choice);
    }
}
