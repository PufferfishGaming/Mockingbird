using System.IO;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Audio.Recording;
using static TriAsr.Ui.SmokeTests.Sound;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The notes page (ADR: notes): the list, the open note that saves itself, and recording into it, with stand-ins for the notes' place, the microphone, the speech program and the keys.</summary>
public sealed class NotesTests
{
    // ---- stand-ins ---------------------------------------------------------------------------------------------------------------

    /// <summary>Notes kept in memory with the same rules as the real ones: a save names the revision it saw. A test can change or delete a note "on another computer".</summary>
    private sealed class FakeNotes : INoteSource
    {
        public Dictionary<Guid, NoteData> Store { get; } = [];
        public List<string> Calls { get; } = [];
        public Func<string, NoteSourceException?>? Fail { get; set; }
        private DateTimeOffset _clock = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

        private DateTimeOffset Now() => _clock = _clock.AddMinutes(1);

        public NoteData Add(string title, string text)
        {
            var note = new NoteData(Guid.NewGuid(), title, text, Now(), 1);
            Store[note.Id] = note;
            return note;
        }

        public void ChangeElsewhere(Guid id, string title, string text) => Store[id] = Store[id] with { Title = title, Text = text, UpdatedUtc = Now(), Revision = Store[id].Revision + 1 };

        public void DeleteElsewhere(Guid id) => Store.Remove(id);

        private void Check(string call) { lock (Calls) Calls.Add(call); if (Fail?.Invoke(call) is { } error) throw error; }

        public Task<IReadOnlyList<NoteInfo>> ListAsync(CancellationToken token)
        {
            Check("list");
            return Task.FromResult<IReadOnlyList<NoteInfo>>(Store.Values.OrderByDescending(note => note.UpdatedUtc).Select(note => new NoteInfo(note.Id, note.Title, NoteText.Preview(note.Text), note.UpdatedUtc, note.Revision)).ToArray());
        }

        public Task<NoteData> GetAsync(Guid id, CancellationToken token)
        {
            Check("get");
            return Task.FromResult(Store.TryGetValue(id, out var note) ? note : throw new NoteSourceException(NoteFailure.Missing, NoteMessages.NotFound));
        }

        public Task<NoteData> CreateAsync(string title, string text, CancellationToken token)
        {
            Check("create");
            return Task.FromResult(Add(title, text));
        }

        public Task<NoteData> SaveAsync(Guid id, string title, string text, int revision, CancellationToken token)
        {
            Check("save");
            if (!Store.TryGetValue(id, out var note)) throw new NoteSourceException(NoteFailure.Missing, NoteMessages.NotFound);
            if (note.Revision != revision) throw new NoteSourceException(NoteFailure.Changed, NoteMessages.Changed);
            return Task.FromResult(Store[id] = note with { Title = title, Text = text, UpdatedUtc = Now(), Revision = revision + 1 });
        }

        public Task DeleteAsync(Guid id, CancellationToken token)
        {
            Check("delete");
            if (!Store.Remove(id)) throw new NoteSourceException(NoteFailure.Missing, NoteMessages.NotFound);
            return Task.CompletedTask;
        }
    }

    private sealed class Screen : IOverlayPresenter
    {
        public int Shown { get; private set; }
        public int Hidden { get; private set; }
        public void ShowOverlay(ILiveOverlay model) => Shown++;
        public void HideOverlay() => Hidden++;
    }

    private sealed class Setup(string root) : IDisposable
    {
        public FakeNotes Source { get; } = new();
        public bool Connected { get; set; } = true;
        public FakeRoom Microphone { get; } = new();
        public FakeReader Speech { get; } = new();
        public Screen Overlay { get; } = new();
        public FakeHotkeys Hotkeys { get; } = new();
        public DictationEngine Engine { get; set; } = null!;
        public NotesSettingsStore Store { get; } = new(root);
        public NotesViewModel Model { get; set; } = null!;
        public string Root => root;
        public void Dispose() { Model.Dispose(); TestCleanup.Delete(root); }
    }

    private static Setup Make(string? root = null, bool ownsHotkey = true)
    {
        var setup = new Setup(root ?? Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")));
        setup.Engine = new DictationEngine(setup.Speech);
        setup.Model = Build(setup, ownsHotkey);
        return setup;
    }

    private static NotesViewModel Build(Setup setup, bool ownsHotkey = true) =>
        new(() => setup.Connected ? setup.Source : null, setup.Microphone, () => setup.Engine, setup.Store, action => action(), ownsHotkey) { Presenter = setup.Overlay, Hotkeys = setup.Hotkeys };

    private static async Task UntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 300; i++) { if (condition()) return; await Task.Delay(30); }
        throw new TimeoutException("Not reached: " + because);
    }

    private static async Task OpenAsync(NotesViewModel model, Guid id)
    {
        model.SelectedNote = model.Notes.First(row => row.Id == id);
        await model.Settled;
    }

    // ---- the list and one note --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheListShowsTheNotesNewestFirstAndANewNoteIsMadeAndOpened()
    {
        using var setup = Make();
        var older = setup.Source.Add("Older", "first");
        var newer = setup.Source.Add("", "Call the dentist about the appointment.");
        await setup.Model.RefreshAsync();
        Assert.Equal([newer.Id, older.Id], setup.Model.Notes.Select(row => row.Id));
        Assert.Equal("Call the dentist about the appointment.", setup.Model.Notes[0].DisplayTitle);       // no title: the first words
        Assert.Equal("Older", setup.Model.Notes[1].DisplayTitle);
        Assert.Equal("first", setup.Model.Notes[1].PreviewLine);
        Assert.Equal("", setup.Model.Notes[0].PreviewLine);                                                           // those words already are the title
        Assert.False(setup.Model.HasOpenNote);
        Assert.True(setup.Model.NoNoteOpen);

        await setup.Model.NewNoteCommand.ExecuteAsync(null);
        Assert.Equal(3, setup.Model.Notes.Count);
        Assert.True(setup.Model.HasOpenNote);
        Assert.Same(setup.Model.Notes[0], setup.Model.SelectedNote);
        Assert.Equal("Untitled note", setup.Model.Notes[0].DisplayTitle);
        Assert.Equal("", setup.Model.Text);
    }

    [Fact]
    public async Task WhatIsTypedIsSavedAndTheListFollows()
    {
        using var setup = Make();
        var note = setup.Source.Add("", "");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, note.Id);
        setup.Model.Title = "Groceries";
        setup.Model.Text = "Milk and bread.\nEggs.";
        await setup.Model.SaveNowAsync();
        Assert.Equal(("Groceries", "Milk and bread.\nEggs.", 2), (setup.Source.Store[note.Id].Title, setup.Source.Store[note.Id].Text, setup.Source.Store[note.Id].Revision));
        Assert.Equal("Groceries", setup.Model.Notes[0].DisplayTitle);
        Assert.Equal("Milk and bread. Eggs.", setup.Model.Notes[0].PreviewLine);
        Assert.Equal("All changes are saved", setup.Model.Status);

        var calls = setup.Source.Calls.Count;
        await setup.Model.SaveNowAsync();                                                                      // nothing changed: nothing is sent
        Assert.Equal(calls, setup.Source.Calls.Count);

        setup.Model.Text += " Butter.";                                                                        // and the next save names the revision the first one made
        await setup.Model.SaveNowAsync();
        Assert.Equal(3, setup.Source.Store[note.Id].Revision);
    }

    [Fact]
    public async Task OpeningAnotherNoteSavesTheOneThatWasOpenFirst()
    {
        using var setup = Make();
        var first = setup.Source.Add("A", "alpha");
        var second = setup.Source.Add("B", "beta");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, first.Id);
        setup.Model.Text = "alpha, changed";                                                                   // not saved yet
        await OpenAsync(setup.Model, second.Id);
        Assert.Equal("alpha, changed", setup.Source.Store[first.Id].Text);
        Assert.Equal("beta", setup.Model.Text);
        Assert.Equal("B", setup.Model.Title);
        Assert.Equal("All changes are saved", setup.Model.Status);                                              // opening a note is not a change
    }

    [Fact]
    public async Task ANoteSavedOnAnotherComputerIsNeverOverwrittenAndWhatWasWrittenHereIsKept()
    {
        using var setup = Make();
        var note = setup.Source.Add("Plan", "version one");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, note.Id);
        setup.Model.Text = "version one, edited here";
        setup.Source.ChangeElsewhere(note.Id, "Plan", "version two, written there");
        await setup.Model.SaveNowAsync();

        Assert.Equal("version two, written there", setup.Source.Store[note.Id].Text);                            // theirs stands
        var copy = Assert.Single(setup.Source.Store.Values, other => other.Id != note.Id);
        Assert.Equal("version one, edited here", copy.Text);                                                      // and mine is a note of its own
        Assert.Equal("Plan (my version)", copy.Title);
        Assert.Equal("version two, written there", setup.Model.Text);                                             // the editor shows what is saved
        Assert.StartsWith("This note was changed on another computer.", setup.Model.Notice);
        Assert.Contains("\"Plan (my version)\"", setup.Model.Notice);
        Assert.Equal(2, setup.Model.Notes.Count);
    }

    [Fact]
    public async Task ANoteDeletedOnAnotherComputerIsSavedAgain()
    {
        using var setup = Make();
        var note = setup.Source.Add("Doomed", "words");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, note.Id);
        setup.Model.Text = "words, and more words";
        setup.Source.DeleteElsewhere(note.Id);
        await setup.Model.SaveNowAsync();
        var again = Assert.Single(setup.Source.Store.Values);
        Assert.Equal(("Doomed", "words, and more words"), (again.Title, again.Text));
        Assert.Equal("This note was deleted on another computer. It was saved again.", setup.Model.Notice);
        Assert.Equal(again.Id, setup.Model.SelectedNote!.Id);
        Assert.Single(setup.Model.Notes);
        setup.Model.Text += " Even more.";
        await setup.Model.SaveNowAsync();                                                                          // the next save goes to the new note
        Assert.EndsWith("Even more.", setup.Source.Store[again.Id].Text);
    }

    [Fact]
    public async Task ASaveThatFailsIsShownKeptAndTriedAgain()
    {
        using var setup = Make();
        var note = setup.Source.Add("Fragile", "text");
        var lost = new List<string>();
        setup.Model.ConnectionLost += reason => lost.Add(reason);
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, note.Id);
        setup.Model.Text = "text, edited";
        setup.Source.Fail = call => call == "save" ? new NoteSourceException(NoteFailure.Unreachable, "The server could not be reached: no route") : null;
        await setup.Model.SaveNowAsync();
        Assert.Equal("The note could not be saved: The server could not be reached: no route", setup.Model.Status);
        Assert.Equal(["The server could not be reached: no route"], lost);
        Assert.Equal("text", setup.Source.Store[note.Id].Text);

        setup.Source.Fail = null;
        await setup.Model.SaveNowAsync();
        Assert.Equal("text, edited", setup.Source.Store[note.Id].Text);
        Assert.Equal("All changes are saved", setup.Model.Status);
    }

    [Fact]
    public async Task ADeletedNoteIsGoneFromTheListAndTheEditor()
    {
        using var setup = Make();
        var keep = setup.Source.Add("Keep", "k");
        var drop = setup.Source.Add("Drop", "d");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, drop.Id);
        await setup.Model.DeleteOpenAsync();
        Assert.Equal([keep.Id], setup.Source.Store.Keys);
        Assert.Equal([keep.Id], setup.Model.Notes.Select(row => row.Id));
        Assert.False(setup.Model.HasOpenNote);
        Assert.Equal("", setup.Model.Text);
        Assert.Null(setup.Model.SelectedNote);
    }

    [Fact]
    public async Task TheListFollowsWhatOtherWindowsDoAndTheOpenNoteIsReloadedOnlyWhenItWasNotChangedHere()
    {
        using var setup = Make();
        var one = setup.Source.Add("One", "1");
        var two = setup.Source.Add("Two", "2");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, one.Id);

        setup.Source.ChangeElsewhere(one.Id, "One, renamed", "1, and more");                                       // someone else saved the open note, which is unchanged here
        var three = setup.Source.Add("Three", "3");
        setup.Source.DeleteElsewhere(two.Id);
        await setup.Model.RefreshAsync();
        Assert.Equal([three.Id, one.Id], setup.Model.Notes.Select(row => row.Id));                                  // the one changed last first (three was made after one was changed)
        Assert.Equal("1, and more", setup.Model.Text);
        Assert.Equal("One, renamed", setup.Model.Title);

        setup.Model.Text = "typing here";
        setup.Source.ChangeElsewhere(one.Id, "One, renamed again", "elsewhere again");
        await setup.Model.RefreshAsync();
        Assert.Equal("typing here", setup.Model.Text);                                                             // what is being written is not replaced
        Assert.Equal("One, renamed", setup.Model.Notes.First(row => row.Id == one.Id).DisplayTitle);
    }

    [Fact]
    public async Task WithNoServerThereIsNothingToShowAndRecordingSaysWhy()
    {
        using var setup = Make();
        setup.Source.Add("Some", "note");
        await setup.Model.RefreshAsync();
        Assert.Single(setup.Model.Notes);
        setup.Connected = false;
        await setup.Model.SourceChangedAsync();
        Assert.Empty(setup.Model.Notes);
        Assert.True(setup.Model.NoSource);
        Assert.Equal("Connect to a server to use its notes.", setup.Model.NoSourceText);
        await setup.Model.StartRecordingAsync();
        Assert.False(setup.Model.IsListening);
        Assert.Equal("Connect to a server to use its notes.", setup.Model.Status);
        Assert.False(setup.Microphone.Open);
    }

    // ---- recording into a note ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryPhraseIsAddedToTheEndOfTheOpenNoteInTheOrderItWasSpokenAndTheNoteIsSaved()
    {
        using var setup = Make();
        var note = setup.Source.Add("Meeting", "Agenda:");
        setup.Speech.Read = async (number, _, _) => { if (number == 1) await Task.Delay(300); return number == 1 ? "Welcome." : "Next point."; };
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, note.Id);
        setup.Model.SelectedLanguage = "de";
        await setup.Model.StartRecordingAsync();
        Assert.True(setup.Model.IsListening);
        Assert.True(setup.Microphone.Open);
        Assert.False(setup.Model.CanSwitch);
        Assert.Equal("Stop recording", setup.Model.RecordButtonLabel);
        Assert.Equal("Listening…", setup.Model.Status);

        setup.Microphone.Hear(Join(Phrase(), Phrase()));
        await UntilAsync(() => setup.Model.Text == "Agenda: Welcome. Next point.", "both phrases are in the note");
        Assert.Equal("Next point.", setup.Model.LastText);
        Assert.All(setup.Speech.Calls, call => Assert.Equal("de", call.Language));

        await setup.Model.StopRecordingAsync();
        Assert.False(setup.Model.IsListening);
        Assert.False(setup.Microphone.Open);
        Assert.Equal("Agenda: Welcome. Next point.", setup.Source.Store[note.Id].Text);                           // saved when recording stopped
        Assert.Equal("All changes are saved", setup.Model.Status);
        Assert.Equal("Record", setup.Model.RecordButtonLabel);
        Assert.True(setup.Model.CanSwitch);
    }

    [Fact]
    public async Task StoppingAddsThePhraseThatWasStillBeingSaid()
    {
        using var setup = Make();
        var note = setup.Source.Add("", "");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, note.Id);
        await setup.Model.StartRecordingAsync();
        setup.Microphone.Hear(Join(Quiet(0.5), Tone(1.5, 0.08)));                                                // no pause after it: the speaker presses stop at once
        Assert.Equal("", setup.Model.Text);
        await setup.Model.StopRecordingAsync();
        Assert.Equal("Hello there.", setup.Model.Text);
        Assert.Equal("Hello there.", setup.Source.Store[note.Id].Text);
    }

    [Fact]
    public async Task RecordingWithNoNoteOpenMakesANewOne()
    {
        using var setup = Make();
        await setup.Model.RefreshAsync();
        await setup.Model.StartRecordingAsync();
        Assert.True(setup.Model.IsListening);
        Assert.True(setup.Model.HasOpenNote);
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Model.Text == "Hello there.", "the phrase is in the note");
        await setup.Model.StopRecordingAsync();
        var note = Assert.Single(setup.Source.Store.Values);
        Assert.Equal("Hello there.", note.Text);
    }

    [Fact]
    public async Task TheListAndTheNewButtonWaitWhileANoteIsRecorded()
    {
        using var setup = Make();
        var note = setup.Source.Add("", "");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, note.Id);
        Assert.True(setup.Model.NewNoteCommand.CanExecute(null));
        await setup.Model.StartRecordingAsync();
        Assert.False(setup.Model.NewNoteCommand.CanExecute(null));
        var before = setup.Source.Calls.Count;
        await setup.Model.NewNoteCommand.ExecuteAsync(null);
        await setup.Model.DeleteOpenAsync();
        Assert.Equal(before, setup.Source.Calls.Count);                                                           // neither did anything
        await setup.Model.StopRecordingAsync();
        Assert.True(setup.Model.NewNoteCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASpeechModelThatIsMissingIsReportedAndNothingIsRecorded()
    {
        using var setup = Make();
        setup.Engine = new DictationEngine(null, Loc.Describe(LiveMessages.NoModel));
        setup.Model.RefreshAvailability();
        Assert.False(setup.Model.IsAvailable);
        Assert.Equal(Loc.Describe(LiveMessages.NoModel), setup.Model.Note);
        await setup.Model.StartRecordingAsync();
        Assert.False(setup.Model.IsListening);
        Assert.Equal(Loc.Describe(LiveMessages.NoModel), setup.Model.Status);
        Assert.Empty(setup.Source.Store);                                                                          // and no empty note was made for it
    }

    [Fact]
    public async Task AMicrophoneThatCannotBeOpenedOrIsUnpluggedStopsRecordingAndSaysWhy()
    {
        using var setup = Make();
        var note = setup.Source.Add("", "kept");
        await setup.Model.RefreshAsync();
        await OpenAsync(setup.Model, note.Id);
        setup.Microphone.CannotOpen = new MicrophoneException("The microphone is being used by another program. Close that program, or choose another microphone.");
        await setup.Model.StartRecordingAsync();
        Assert.False(setup.Model.IsListening);
        Assert.StartsWith("The microphone is being used by another program", setup.Model.Status);

        setup.Microphone.CannotOpen = null;
        await setup.Model.StartRecordingAsync();
        Assert.True(setup.Model.IsListening);
        setup.Microphone.Unplug();
        await UntilAsync(() => !setup.Model.IsListening, "recording stops");
        await UntilAsync(() => setup.Model.Status.StartsWith("The microphone is being used"), "the reason is shown");
    }

    [Fact]
    public async Task APhraseTheSpeechProgramCannotReadIsReportedAndTheNextOneStillGoesIn()
    {
        using var setup = Make();
        var calls = 0;
        setup.Speech.Read = (_, _, _) => ++calls == 1 ? throw new LiveException(LiveMessages.Failed) : Task.FromResult("Second try.");
        await setup.Model.RefreshAsync();
        await setup.Model.StartRecordingAsync();
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Model.Status == LiveMessages.Failed, "the problem is shown");
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Model.Text == "Second try.", "the next phrase goes in");
        await setup.Model.StopRecordingAsync();
    }

    [Theory]
    [InlineData("", "Hello.", "Hello.")]
    [InlineData("Hello.", "World.", "Hello. World.")]
    [InlineData("Hello.\n", "World.", "Hello.\nWorld.")]
    [InlineData("Hello. ", "World.", "Hello. World.")]
    [InlineData("今日は。", "元気です。", "今日は。元気です。")]
    [InlineData("Hello.", "元気です。", "Hello.元気です。")]
    [InlineData("Hello.", "", "Hello.")]
    public void WordsAreAddedWithASpaceExceptWhereNoSpaceIsWritten(string text, string words, string expected) =>
        Assert.Equal(expected, PhraseText.Append(text, words));

    // ---- the keys and the little window ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheKeysOfRecordingWorkInEveryProgramAndShowTheLittleWindowWhileTheyRecord()
    {
        using var setup = Make();
        Assert.False(setup.Hotkeys.Registered.ContainsKey(NotesViewModel.HotkeyAction));                           // no keys until the person chooses some
        Assert.True(setup.Model.Keybind.Current.IsNone);
        FakeHotkeys.Choose(setup.Model.Keybind, new KeyCombo(KeyCombo.Control | KeyCombo.Alt, 0x4E));              // Ctrl+Alt+N
        Assert.Equal("Ctrl+Alt+N", setup.Hotkeys.Registered[NotesViewModel.HotkeyAction].Id);

        setup.Hotkeys.Press(NotesViewModel.HotkeyAction);
        await UntilAsync(() => setup.Model.IsListening, "recording starts");
        Assert.Equal(1, setup.Overlay.Shown);
        Assert.True(setup.Model.IsOverlayVisible);
        Assert.True(setup.Model.HasOpenNote);                                                                      // a note was made for it
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Model.Text == "Hello there.", "the words are in the note");

        setup.Hotkeys.Press(NotesViewModel.HotkeyAction);
        await UntilAsync(() => !setup.Model.IsListening && !setup.Model.IsOverlayVisible, "recording stops and the window goes");
        Assert.Equal(1, setup.Overlay.Hidden);
        Assert.Equal("Hello there.", Assert.Single(setup.Source.Store.Values).Text);
    }

    [Fact]
    public async Task TheLittleWindowsCloseButtonStopsRecordingAndKeysThatAreTakenAreReported()
    {
        using var setup = Make();
        setup.Hotkeys.RegisterResult = false;
        FakeHotkeys.Choose(setup.Model.Keybind, new KeyCombo(KeyCombo.Control | KeyCombo.Alt, 0x4E));
        Assert.Equal("The keys Ctrl+Alt+N are used by another program. Choose other keys.", setup.Model.HotkeyNote);
        setup.Hotkeys.RegisterResult = true;
        FakeHotkeys.Choose(setup.Model.Keybind, new KeyCombo(0, 0x7A));                                           // F11
        Assert.Equal("", setup.Model.HotkeyNote);

        setup.Hotkeys.Press(NotesViewModel.HotkeyAction);
        await UntilAsync(() => setup.Model.IsListening, "recording starts");
        await setup.Model.HideOverlayAsync();                                                                       // the window's own close button
        Assert.False(setup.Model.IsListening);
        Assert.False(setup.Model.IsOverlayVisible);
        Assert.Equal(1, setup.Overlay.Hidden);
    }

    [Fact]
    public async Task TheLittleWindowShowsWhyNothingIsRecordedWhenTheKeysAreUsedWithoutASpeechModel()
    {
        using var setup = Make();
        setup.Engine = new DictationEngine(null, Loc.Describe(LiveMessages.NoModel));
        FakeHotkeys.Choose(setup.Model.Keybind, new KeyCombo(KeyCombo.Control | KeyCombo.Alt, 0x4E));
        setup.Hotkeys.Press(NotesViewModel.HotkeyAction);
        await UntilAsync(() => setup.Model.IsOverlayVisible, "the window appears");
        Assert.False(setup.Model.IsListening);
        Assert.Equal(Loc.Describe(LiveMessages.NoModel), setup.Model.Status);                                       // the window says why, so that a press of the keys is not met with silence
        await setup.Model.HideOverlayAsync();
        Assert.False(setup.Model.IsOverlayVisible);
    }

    [Fact]
    public void NotesOfAServerDoNotTakeTheKeysThatBelongToStudiosOwnNotes()
    {
        using var setup = Make(ownsHotkey: false);
        Assert.False(setup.Model.ShowsKeybind);
        setup.Hotkeys.Claims.Clear();
        FakeHotkeys.Choose(setup.Model.Keybind, new KeyCombo(KeyCombo.Control | KeyCombo.Alt, 0x4E));
        Assert.Empty(setup.Hotkeys.Registered);
        Assert.DoesNotContain(NotesViewModel.HotkeyAction, setup.Hotkeys.Claims.Keys);
    }

    [Fact]
    public void KeysThatDictationHasCannotBeChosenForNotes()
    {
        using var setup = Make();
        setup.Hotkeys.Claim(DictationViewModel.HotkeyAction, DictationViewModel.DefaultKeys);
        var keys = setup.Model.Keybind;
        keys.BeginCommand.Execute(null);
        keys.Press(0x20, KeyCombo.Control | KeyCombo.Alt);
        keys.DoneCommand.Execute(null);
        Assert.True(keys.IsCapturing);
        Assert.StartsWith("The keys Ctrl+Alt+Space are used by another program, or already have a use here", keys.Message);
        keys.CancelCommand.Execute(null);
        Assert.Equal(0, setup.Hotkeys.Paused);
    }

    // ---- what is remembered ------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhatWasChosenIsRememberedAndTheOtherPagesChoicesAreNotUndone()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        using (var first = Make(root))
        {
            using var second = Make(root, ownsHotkey: false);                                                       // the notes of the server, in the same window
            first.Model.SelectedLanguage = "hu";
            FakeHotkeys.Choose(first.Model.Keybind, new KeyCombo(KeyCombo.Control | KeyCombo.Alt, 0x4E));
            second.Model.SelectedDevice = second.Model.Devices.First(device => device.Id == 1);                    // another page changes something else afterwards
            first.Model.RememberPosition(120.5, 340);
            first.Model.Dispose();
            var again = Build(first);
            Assert.Equal("hu", again.SelectedLanguage);
            Assert.Equal("Ctrl+Alt+N", again.Keybind.Current.Id);
            Assert.Equal(1, again.SelectedDevice!.Id);
            Assert.Equal((120.5, 340d), again.Position);
        }
        var store = new NotesSettingsStore(root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{\"language\":\"\",\"hotkey\":\"A\",\"microphone\":-1}");
        Assert.Equal("auto", store.Load().Language);
        Assert.Equal("", store.Load().Hotkey);                                                                      // a letter on its own cannot be a hotkey: no keys
        File.WriteAllText(store.FilePath, "this is not json");
        Assert.Equal(new NotesSettings(), store.Load());
        Assert.Equal("Ctrl+Alt+Space", NotesSettings.Normalize(new NotesSettings(Hotkey: "ctrl-alt-space")).Hotkey);
        Assert.Null(NotesSettings.Normalize(new NotesSettings(Left: double.NaN, Top: double.PositiveInfinity)).Left);
        TestCleanup.Delete(root);
    }

    [Fact]
    public async Task TheTextsAreSaidInTheInterfaceLanguageAfterItChanges()
    {
        var before = Loc.Instance.Language;
        try
        {
            using var setup = Make();
            var note = setup.Source.Add("", "words");
            await setup.Model.RefreshAsync();
            Assert.Equal("Record", setup.Model.RecordButtonLabel);
            Loc.Instance.SetLanguage("de");
            setup.Model.RefreshTexts();
            Assert.Equal("Aufnehmen", setup.Model.RecordButtonLabel);
            Assert.Equal("words", setup.Model.Notes[0].DisplayTitle);
            setup.Source.Add("", "");
            await setup.Model.RefreshAsync();
            Assert.Equal("Unbenannte Notiz", setup.Model.Notes.First(row => row.Info.Preview.Length == 0).DisplayTitle);
            Assert.Equal(note.Id, setup.Model.Notes.First(row => row.Info.Preview == "words").Id);
        }
        finally { Loc.Instance.SetLanguage(before); }
    }
}
