using CommunityToolkit.Mvvm.ComponentModel;
using TriAsr.Domain;
using TriAsr.Export;

namespace TriAsr.App;

public sealed partial class ReviewRegion(FinalRegion original, string? machineText = null) : ObservableObject
{
    public FinalRegion Original { get; private set; } = original;
    public string MachineText { get; } = machineText ?? original.FinalText;
    [ObservableProperty] private string _text = original.FinalText;
    public string Time => Original.NativeTimestamps ? TranscriptExporter.Timestamp(Original.StartMs) : "No timestamps";
    public string Whisper => Original.WhisperText;
    public string Canary => Original.CanaryText;
    public string CanaryHeading => Original.NativeTimestamps ? "CANARY · projected onto Whisper timing" : "CANARY · original untimed text";
    public string Source => Original.Source;
    public bool IsUncertain => Original.Source is "uncertain" or "single-asr-needs-listening" || Original.Warnings?.Count > 0;
    public string Evidence => $"{Source} · {Original.LlmChoice ?? "—"} · confidence {Original.Confidence?.ToString("0.00") ?? "—"}" +
        (Original.Warnings?.Count > 0 ? "\n" + string.Join("\n", Original.Warnings) : "");
    public FinalRegion Snapshot()
    {
        if (Text == Original.FinalText) return Original;
        var revisions = (Original.Revisions ?? []).Append(new ManualRevision(DateTimeOffset.UtcNow, Original.FinalText, Text)).ToArray();
        return Original with { FinalText = Text, Source = "manual", Revisions = revisions };
    }
    public void AcceptSaved() { Original = Snapshot(); OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(Evidence)); OnPropertyChanged(nameof(IsUncertain)); }
}
