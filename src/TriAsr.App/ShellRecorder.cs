using System.IO;
using TriAsr.Audio.Recording;

namespace TriAsr.App;

/// <summary>Recording from the microphone for Studio's New transcription page: the finished recording becomes the file to transcribe.</summary>
public sealed partial class ShellViewModel
{
    private RecorderViewModel? _recorder;

    public RecorderViewModel Recorder => _recorder ??= MakeRecorder();

    /// <summary>What recordings are made from. Only a test changes it, and before the recorder is first used.</summary>
    public IMicrophone Microphone { get; set; } = new WindowsMicrophone();

    /// <summary>Whether a recording is running now (it is not made just to ask).</summary>
    public bool IsRecordingNow => _recorder?.IsRecording == true;

    private RecorderViewModel MakeRecorder()
    {
        var recorder = new RecorderViewModel(Microphone, Path.Combine(storage.Root, "Recordings"), OnUi);
        recorder.Recorded += file =>
        {
            SourcePath = file.Path;
            Status = T("Recording saved: {0} ({1})", Path.GetFileName(file.Path), file.Duration.ToString(file.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));
        };
        return recorder;
    }

    /// <summary>The window is closing: a recording that is running is saved, not lost.</summary>
    public void StopRecordingForExit() => _recorder?.StopForExit();
}
