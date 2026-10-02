using TriAsr.Application;

namespace TriAsr.App;

/// <summary>Live dictation in Studio: the phrases are read by this computer's own speech program.</summary>
public sealed partial class ShellViewModel
{
    private DictationViewModel? _dictation;

    /// <summary>Where the words go. Only a test changes it, and before dictation is first used.</summary>
    public ITextOutput DictationOutput { get; set; } = new WindowsKeyboard();

    public DictationViewModel Dictation => _dictation ??= MakeDictation();

    private DictationViewModel MakeDictation() => new(Microphone,
        () => live.IsReady ? new DictationEngine(live) : new DictationEngine(null, Loc.Describe(LiveMessages.NoModel)),
        DictationOutput, new DictationSettingsStore(storage.Root), OnUi);

    /// <summary>Whether dictation is listening now; the program does not update itself then.</summary>
    public bool IsDictating => _dictation?.IsListening == true;

    /// <summary>The window is closing: the microphone is let go of and the little window goes away.</summary>
    public void StopDictationForExit() => _dictation?.Dispose();
}
