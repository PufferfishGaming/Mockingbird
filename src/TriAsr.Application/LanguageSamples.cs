using TriAsr.Domain;

namespace TriAsr.Application;

/// <summary>Chooses where the 15 second samples for language detection are taken, so they land on speech and not on a quiet start or end.</summary>
public static class LanguageSamples
{
    public const double WindowSeconds = 15;

    /// <summary>
    /// The start (in seconds) of up to three samples: the first, the middle and the last speech chunk, counted by speech time.
    /// Null when the plan has no speech or does not describe a recording of this length, so the caller keeps its usual positions.
    /// </summary>
    public static IReadOnlyList<double>? From(ChunkPlan plan, double audioSeconds)
    {
        if (plan.Version != ChunkPlan.CurrentVersion || Math.Abs(plan.DurationMs / 1000d - audioSeconds) > 0.01) return null;
        var speech = plan.SpeechChunks().ToList();
        if (speech.Count == 0) return null;
        var half = speech.Sum(chunk => chunk.DurationMs) / 2;
        long seen = 0; AudioChunk middle = speech[0];
        foreach (var chunk in speech) { seen += chunk.DurationMs; if (seen >= half) { middle = chunk; break; } }
        var picks = audioSeconds <= 2 * WindowSeconds ? [speech[0]] : new[] { speech[0], middle, speech[^1] }.DistinctBy(chunk => chunk.Index).ToArray();
        var latest = Math.Max(0, audioSeconds - WindowSeconds);
        return picks.Select(chunk => Math.Round(Math.Min(chunk.StartMs / 1000d, latest), 3)).Distinct().ToList();
    }
}
