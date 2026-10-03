using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>A choice in the Speakers list of a new transcription: not to tell the speakers apart, to tell them apart, or how many there are.</summary>
/// <param name="Value">As a job keeps it (<see cref="JobOptions.Speakers"/>): <c>off</c>, <c>auto</c> or a number.</param>
/// <param name="Name">What the list shows; the words are translated, a number is shown as it is.</param>
public sealed record SpeakerChoice(string Value, string Name)
{
    public override string ToString() => Name;

    /// <summary>The choices every page offers, in this order.</summary>
    public static IReadOnlyList<SpeakerChoice> All { get; } =
    [
        new("off", Loc.Key("Don't tell speakers apart")),
        new("auto", Loc.Key("Tell speakers apart")),
        .. Enumerable.Range(2, JobOptions.MostSpeakers - 1).Select(count => new SpeakerChoice(count.ToString(System.Globalization.CultureInfo.InvariantCulture), count.ToString(System.Globalization.CultureInfo.InvariantCulture)))
    ];
}
