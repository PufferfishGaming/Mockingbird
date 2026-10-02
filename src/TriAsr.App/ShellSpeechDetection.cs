using CommunityToolkit.Mvvm.ComponentModel;

namespace TriAsr.App;

/// <summary>The Settings choice of whether both engines leave out stretches without speech. Off by default because singing counts as non-speech.</summary>
public sealed partial class ShellViewModel
{
    [ObservableProperty] private bool _skipNonSpeech;

    partial void OnSkipNonSpeechChanged(bool value)
    {
        if (_initialized) Persist();
    }

    /// <summary>
    /// Whether a small AI model may choose between Whisper and Canary where they disagree. Off by default: since its redesign (ADR-0008) it never
    /// gets an answer rejected and changes Whisper's wording only at 90% or more, but no measurement has shown that it improves a transcript.
    /// </summary>
    [ObservableProperty] private bool _useCorrectionModel;

    partial void OnUseCorrectionModelChanged(bool value)
    {
        if (_initialized) Persist();
    }

    private void RestoreSpeechDetectionSettings(AppSettings settings)
    {
        SkipNonSpeech = settings.SkipNonSpeech;
        UseCorrectionModel = settings.UseCorrectionModel;
    }
}
