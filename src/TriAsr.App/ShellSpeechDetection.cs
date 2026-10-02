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
    /// Whether a small AI model may choose between Whisper and Canary where they disagree. On by default (the maintainer's decision):
    /// since its redesign it never gets an answer rejected and changes Whisper's wording only at 90% or more, though no measurement has
    /// yet shown that it improves a transcript. While its model is not downloaded, jobs behave as if it were off.
    /// </summary>
    [ObservableProperty] private bool _useCorrectionModel = true;

    partial void OnUseCorrectionModelChanged(bool value)
    {
        if (_initialized) { Persist(); RefreshReadiness(); }
    }

    private void RestoreSpeechDetectionSettings(AppSettings settings)
    {
        SkipNonSpeech = settings.SkipNonSpeech;
        UseCorrectionModel = settings.UseCorrectionModel;
    }
}
