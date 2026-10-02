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

    private void RestoreSpeechDetectionSettings(AppSettings settings) => SkipNonSpeech = settings.SkipNonSpeech;
}
