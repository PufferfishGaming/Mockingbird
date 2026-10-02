using System.Text.Json;

namespace TriAsr.Engine.Llm;

/// <param name="Choice">"whisper", "canary" or "uncertain". The text is always one of the two candidates, supplied by the code, never by the model.</param>
public sealed record ArbitrationDecision(string Choice, string Text, double Confidence, bool Uncertain);

/// <summary>How likely the model found each one-letter answer: A, B, or U (cannot tell). The three add up to 1.</summary>
public readonly record struct ChoiceProbabilities(double A, double B, double Unsure)
{
    public static ChoiceProbabilities None => new(0, 0, 1);
}

/// <summary>
/// Turns the model's answer into a decision. The model is asked one question, "which option was spoken: A, B or U?", and answers
/// with one token; the code reads the probabilities of the three possible tokens, so there is no text to copy wrongly and no
/// self-reported confidence to trust. The question is asked twice with the options swapped, and the two answers are averaged, which
/// cancels a leaning towards the first option.
/// <para>
/// Whisper's wording is the default. Canary's wording replaces it only when the averaged probability for Canary reaches
/// <see cref="CanaryThreshold"/>. Measured on 75 real disagreements with a known transcript: the earlier design rejected 56% of its
/// answers and of the ten words it changed, nine were mistakes; this design rejects nothing and, at this threshold, changes one word
/// and makes no mistake. A text-only model cannot tell which candidate was sung when both are plausible words, so no gain is claimed.
/// </para>
/// </summary>
public static class ArbitrationScoring
{
    public const double CanaryThreshold = 0.9;
    public const double WhisperThreshold = 0.75;

    /// <summary>Reads the first answer token's probabilities from a chat completion that was asked for log-probabilities.</summary>
    public static ChoiceProbabilities ReadProbabilities(string chatResponseJson)
    {
        using var document = JsonDocument.Parse(chatResponseJson);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("logprobs", out var logprobs) || logprobs.ValueKind != JsonValueKind.Object
            || !logprobs.TryGetProperty("content", out var content) || content.GetArrayLength() == 0
            || !content[0].TryGetProperty("top_logprobs", out var top))
            throw new InvalidDataException("The correction server returned no probabilities.");
        double a = 0, b = 0, u = 0;
        foreach (var entry in top.EnumerateArray())
        {
            var weight = Math.Exp(entry.GetProperty("logprob").GetDouble());
            switch ((entry.GetProperty("token").GetString() ?? "").Trim().ToUpperInvariant())
            {
                case "A": a += weight; break;
                case "B": b += weight; break;
                case "U": u += weight; break;
            }
        }
        var total = a + b + u;
        return total > 0 && double.IsFinite(total) ? new(a / total, b / total, u / total) : ChoiceProbabilities.None;
    }

    /// <param name="whisperFirst">The answer when option A was Whisper's wording and option B Canary's.</param>
    /// <param name="canaryFirst">The answer when the options were swapped: A was Canary's wording.</param>
    public static ArbitrationDecision Decide(string whisper, string canary, ChoiceProbabilities whisperFirst, ChoiceProbabilities canaryFirst)
    {
        // Text-only arbitration cannot prove that words only one engine wrote were spoken.
        if (string.IsNullOrWhiteSpace(whisper) || string.IsNullOrWhiteSpace(canary)) return new("uncertain", whisper, 0, true);
        var pWhisper = (whisperFirst.A + canaryFirst.B) / 2;
        var pCanary = (whisperFirst.B + canaryFirst.A) / 2;
        if (pCanary >= CanaryThreshold) return new("canary", canary, pCanary, false);
        if (pWhisper >= WhisperThreshold) return new("whisper", whisper, pWhisper, false);
        return new("uncertain", whisper, Math.Max(pWhisper, pCanary), true);
    }
}
