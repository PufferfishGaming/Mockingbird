using TriAsr.Application;

namespace TriAsr.App;

/// <summary>Notes in Studio: the notes of this computer, kept in its data folder. The Remote server page has the server's notes.</summary>
public sealed partial class ShellViewModel
{
    private NotesViewModel? _notes;

    /// <summary>The notes of this computer.</summary>
    public NotesViewModel Notes => _notes ??= new(() => new LocalNoteSource(noteStore), Microphone,
        () => live.IsReady ? new DictationEngine(live) : new DictationEngine(null, Loc.Describe(LiveMessages.NoModel)),
        new NotesSettingsStore(storage.Root), OnUi);

    public bool IsNotesPage => SelectedPage?.Name == "Notes";

    /// <summary>Whether a note is being recorded; the program does not update itself then.</summary>
    public bool IsNoting => _notes?.IsListening == true;

    /// <summary>The window is closing: the microphone is let go of, the keys are given back and the open note is saved.</summary>
    public void StopNotesForExit() => _notes?.Dispose();
}