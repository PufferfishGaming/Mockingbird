using CommunityToolkit.Mvvm.ComponentModel;
using TriAsr.Domain;
using TriAsr.Export;

namespace TriAsr.App;

public sealed partial class ReviewRegion(FinalRegion original, string? machineText = null) : ObservableObject
{
    public FinalRegion Original { get; private set; } = original;
    public string MachineText { get; } = machineText ?? original.FinalText;
    [ObservableProperty] private string _text = original.FinalText;
    public string Time => Original.NativeTimestamps ? TranscriptExporter.Timestamp(Original.StartMs) : Loc.T("No timestamps");
    public string Whisper => Original.WhisperText;
    public string Canary => Original.CanaryText;
    public string CanaryHeading => Original.NativeTimestamps ? Loc.T("CANARY · projected onto Whisper timing") : Loc.T("CANARY · original untimed text");
    public string Source => Original.Source;
    public bool IsUncertain => NeedsListening(Original);
    /// <summary>Whether a region has to be listened to before it is trusted (the engines disagreed unresolved, only one engine heard it, or a check flagged it).</summary>
    public static bool NeedsListening(FinalRegion region) => region.Source is "uncertain" or "single-asr-needs-listening" || region.Warnings?.Count > 0;
    public string Evidence => Loc.T("{0} · {1} · confidence {2}", SourceText.Of(Source), Original.LlmChoice ?? "—", Original.Confidence?.ToString("0.00") ?? "—") +
        (Original.Warnings?.Count > 0 ? "\n" + string.Join("\n", Original.Warnings.Select(Loc.T)) : "");
    /// <summary>The texts built from the interface language are read again after the language changes.</summary>
    public void NotifyLanguageChanged() { OnPropertyChanged(nameof(Time)); OnPropertyChanged(nameof(CanaryHeading)); OnPropertyChanged(nameof(Evidence)); }
    public FinalRegion Snapshot()
    {
        if (Text == Original.FinalText) return Original;
        var revisions = (Original.Revisions ?? []).Append(new ManualRevision(DateTimeOffset.UtcNow, Original.FinalText, Text)).ToArray();
        return Original with { FinalText = Text, Source = "manual", Revisions = revisions };
    }
    public void AcceptSaved() { Original = Snapshot(); OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(Evidence)); OnPropertyChanged(nameof(IsUncertain)); }
}
