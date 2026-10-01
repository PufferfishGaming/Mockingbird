using System.Text.Json;
using System.Text.RegularExpressions;

namespace TriAsr.Engine.Llm;

public sealed record ArbitrationDecision(string Choice, string Text, double Confidence, bool Uncertain);

public static partial class ArbitrationValidation
{
    public static ArbitrationDecision Parse(string json, string whisper, string canary)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var key in new[] { "choice", "text", "confidence", "uncertain" }) if (!root.TryGetProperty(key, out _)) throw new InvalidDataException("Arbitration field missing.");
        var result = JsonSerializer.Deserialize<ArbitrationDecision>(json, options) ?? throw new InvalidDataException("Missing arbitration decision.");
        if (result.Choice is not ("whisper" or "canary" or "merged" or "uncertain") || !double.IsFinite(result.Confidence) || result.Confidence < 0 || result.Confidence > 1)
            throw new InvalidDataException("Invalid arbitration choice or confidence.");
        if (result.Choice == "whisper" && result.Text != whisper || result.Choice == "canary" && result.Text != canary)
            throw new InvalidDataException("A selected candidate must be preserved exactly.");
        if (result.Choice == "merged")
        {
            var a = Tokens(whisper); var b = Tokens(canary); var merged = Tokens(result.Text);
            foreach (var token in merged.Distinct())
                if (merged.Count(value => value == token) > Math.Max(a.Count(value => value == token), b.Count(value => value == token)))
                    throw new InvalidDataException("Arbitration introduced unsupported wording.");
        }
        if (result.Choice == "uncertain" && result.Text != whisper && result.Text != canary && result.Text.Length != 0)
            throw new InvalidDataException("Uncertain output introduced wording.");
        // Text-only arbitration cannot prove that one-sided additions were spoken.
        return result with { Uncertain = result.Uncertain || result.Choice == "uncertain" || result.Confidence < 0.75
            || string.IsNullOrWhiteSpace(whisper) || string.IsNullOrWhiteSpace(canary) };
    }
    private static string[] Tokens(string text) => Words().Matches(text.ToLowerInvariant()).Select(match => match.Value).ToArray();
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex Words();
}
