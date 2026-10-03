using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>One speaker of the open transcript and the name the person gives them.</summary>
public sealed partial class SpeakerNameRow(string speaker, string name, Action changed) : ObservableObject
{
    public string Speaker { get; } = speaker;
    [ObservableProperty] private string _name = name;
    /// <summary>What the speaker is called until they have a name ("Speaker 2").</summary>
    public string Heading => Loc.T("Speaker {0}", Speaker);
    public int MaxLength => SpeakerNames.MaxLength;
    partial void OnNameChanged(string value) => changed();
    public void NotifyLanguageChanged() => OnPropertyChanged(nameof(Heading));
}

/// <summary>
/// Naming the speakers of a transcript in review (Studio's Review page and a server's Review tab). The names are kept with the edited transcript; the regions keep
/// their speakers' numbers, so a name can be changed again or taken away.
/// </summary>
public sealed partial class SpeakerNamesEditor : ObservableObject
{
    private IReadOnlyDictionary<string, string>? _saved;

    public ObservableCollection<SpeakerNameRow> Rows { get; } = [];
    public bool HasSpeakers => Rows.Count > 0;

    /// <summary>A name was changed; the regions show it at once.</summary>
    public event Action? Changed;

    /// <summary>Shows the speakers of a transcript with the names it has.</summary>
    public void Load(FinalTranscript transcript)
    {
        Rows.Clear();
        foreach (var speaker in transcript.Speakers) Rows.Add(new SpeakerNameRow(speaker, transcript.NameOf(speaker) ?? "", () => Changed?.Invoke()));
        _saved = Current();
        OnPropertyChanged(nameof(HasSpeakers));
    }

    /// <summary>No transcript is open, or its names cannot be kept: there is nothing to name.</summary>
    public void Clear()
    {
        Rows.Clear();
        _saved = null;
        OnPropertyChanged(nameof(HasSpeakers));
    }

    /// <summary>The names as they stand: the speakers that have one; null when none has.</summary>
    public IReadOnlyDictionary<string, string>? Current() =>
        SpeakerNames.Clean(Rows.ToDictionary(row => row.Speaker, row => row.Name, StringComparer.Ordinal), Rows.Select(row => row.Speaker));

    /// <summary>Shows the names as they stand on the regions of the transcript.</summary>
    public void ShowOn(IEnumerable<ReviewRegion> regions)
    {
        var names = Current();
        foreach (var region in regions) region.ShowSpeakerName(region.Original.Speaker is { } speaker ? names?.GetValueOrDefault(speaker) : null);
    }

    /// <summary>Whether a name was changed since the transcript was opened or saved.</summary>
    public bool IsChanged => !Same(Current(), _saved);

    public void AcceptSaved() => _saved = Current();

    public void RefreshTexts() { foreach (var row in Rows) row.NotifyLanguageChanged(); }

    private static bool Same(IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? second) =>
        (first?.Count ?? 0) == (second?.Count ?? 0) && (first is null || first.All(pair => second?.GetValueOrDefault(pair.Key) == pair.Value));
}
