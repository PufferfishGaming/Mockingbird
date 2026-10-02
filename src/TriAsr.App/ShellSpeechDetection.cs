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
    /// Whether a small AI model may choose between Whisper and Canary where they disagree. Off by default: on a song with a known
    /// transcript every word it changed was a mistake (three of three), and a third of its answers were rejected.
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
