using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Audio.Recording;
using Reader = TriAsr.Ui.SmokeTests.FakeReader;
using Room = TriAsr.Ui.SmokeTests.FakeRoom;
using static TriAsr.Ui.SmokeTests.Sound;

namespace TriAsr.Ui.SmokeTests;

/// <summary>Live dictation (ADR: live dictation) from the microphone to the keyboard, with stand-ins for the microphone, the speech program and the keyboard.</summary>
public sealed class DictationTests
{
    private const int Rate = 16_000;

    // ---- stand-ins ---------------------------------------------------------------------------------------------------------------

    private sealed class Keyboard : ITextOutput
    {
        public List<string> Typed { get; } = [];
        public List<string> Pasted { get; } = [];
        public Exception? Fail { get; set; }
        public void Type(string text) { if (Fail is not null) throw Fail; lock (Typed) Typed.Add(text); }
        public void Paste(string text) { if (Fail is not null) throw Fail; lock (Pasted) Pasted.Add(text); }
    }

    private sealed class Window : IOverlayPresenter
    {
        public int Shown { get; private set; }
        public int Hidden { get; private set; }
        public void ShowOverlay(ILiveOverlay model) => Shown++;
        public void HideOverlay() => Hidden++;
    }

    private sealed class Setup(string root) : IDisposable
    {
        public Room Microphone { get; } = new();
        public Reader Speech { get; } = new();
        public Keyboard Keys { get; } = new();
        public Window Screen { get; } = new();
        public FakeHotkeys Hotkeys { get; } = new();
        public DictationEngine Engine { get; set; } = null!;
        public DictationSettingsStore Store { get; } = new(root);
        public DictationViewModel Model { get; set; } = null!;
        public string Root => root;
        public void Dispose() { Model.Dispose(); TestCleanup.Delete(root); }
    }

    private static Setup Make(string? root = null)
    {
        var setup = new Setup(root ?? Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")));
        setup.Engine = new DictationEngine(setup.Speech);
        setup.Model = Build(setup);
        return setup;
    }

    private static DictationViewModel Build(Setup setup) =>
        new(setup.Microphone, () => setup.Engine, setup.Keys, setup.Store, action => action()) { Presenter = setup.Screen, Hotkeys = setup.Hotkeys };

    private static async Task UntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 300; i++) { if (condition()) return; await Task.Delay(30); }
        throw new TimeoutException("Not reached: " + because);
    }

    // ---- the path from the voice to the keyboard ------------------------------------------------------------------------------------

    [Fact]
    public async Task ASpokenPhraseIsReadAndTypedWithASpaceAfterIt()
    {
        using var setup = Make();
        setup.Model.SelectedLanguage = "de";
        await setup.Model.StartAsync();
        Assert.True(setup.Model.IsListening);
        Assert.True(setup.Microphone.Open);
        Assert.Equal("Listening…", setup.Model.Status);

        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Keys.Typed.Count == 1, "the phrase is typed");
        Assert.Equal("Hello there. ", setup.Keys.Typed[0]);
        Assert.Equal("Hello there.", setup.Model.LastText);
        var call = Assert.Single(setup.Speech.Calls);
        Assert.Equal("de", call.Language);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(call.Wav, 0, 4));          // what the speech program is given is a WAV file
        Assert.InRange((double)call.Wav.Length, 44 + 1.2 * Rate * 2, 44 + 2.4 * Rate * 2);

        await setup.Model.StopAsync();
        Assert.False(setup.Model.IsListening);
        Assert.False(setup.Microphone.Open);
        Assert.Equal("Press the button or Ctrl+Alt+Space to start dictating.", setup.Model.Status);
    }

    [Fact]
    public async Task PhrasesAreTypedInTheOrderTheyWereSpokenEvenWhenAnEarlierOneIsSlowerToRead()
    {
        using var setup = Make();
        setup.Speech.Read = async (number, _, _) => { if (number == 1) await Task.Delay(400); return number == 1 ? "First." : "Second."; };
        await setup.Model.StartAsync();
        setup.Microphone.Hear(Join(Phrase(), Phrase()));
        await UntilAsync(() => setup.Keys.Typed.Count == 2, "both phrases are typed");
        Assert.Equal(["First. ", "Second. "], setup.Keys.Typed);
        await setup.Model.StopAsync();
    }

    [Fact]
    public async Task StoppingReadsAndTypesThePhraseThatWasStillBeingSaid()
    {
        using var setup = Make();
        await setup.Model.StartAsync();
        setup.Microphone.Hear(Join(Quiet(0.5), Tone(1.5, 0.08)));                      // no pause after it: the speaker presses stop at once
        Assert.Empty(setup.Keys.Typed);
        await setup.Model.StopAsync();                                                     // returns when the last phrase has been dealt with
        Assert.Equal(["Hello there. "], setup.Keys.Typed);
    }

    [Fact]
    public async Task WordsWhisperInventsForSoundWithoutSpeechAreNotTypedButTheSameWordsInALongPhraseAre()
    {
        using var setup = Make();
        var answers = new Queue<string>(["you", "Thank you.", "[BLANK_AUDIO]", "Thank you."]);
        setup.Speech.Read = (_, _, _) => Task.FromResult(answers.Dequeue());
        await setup.Model.StartAsync();
        setup.Microphone.Hear(Join(Quiet(0.5), Tone(0.4, 0.08), Quiet(1.2)));        // a short sound: "you"
        setup.Microphone.Hear(Join(Tone(0.5, 0.08), Quiet(1.2)));                       // "Thank you." after half a second
        setup.Microphone.Hear(Join(Tone(0.5, 0.08), Quiet(1.2)));                       // only a marker
        setup.Microphone.Hear(Join(Tone(2.5, 0.08), Quiet(1.2)));                       // "Thank you." said at length is a sentence
        await UntilAsync(() => setup.Speech.Calls.Count == 4, "all four are read");
        await setup.Model.StopAsync();
        Assert.Equal(["Thank you. "], setup.Keys.Typed);
    }

    [Fact]
    public async Task APhraseThatCannotBeReadIsReportedAndListeningGoesOn()
    {
        using var setup = Make();
        var calls = 0;
        setup.Speech.Read = (_, _, _) => ++calls == 1 ? throw new LiveException(LiveMessages.Failed) : Task.FromResult("Second try.");
        await setup.Model.StartAsync();
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Model.Status == LiveMessages.Failed, "the problem is shown");
        Assert.True(setup.Model.IsListening);
        Assert.Empty(setup.Keys.Typed);
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Keys.Typed.Count == 1, "the next phrase is typed");
        Assert.Equal("Listening…", setup.Model.Status);
        await setup.Model.StopAsync();

        setup.Speech.Read = (_, _, _) => throw new IOException("The disk is full.");
        await setup.Model.StartAsync();
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Model.Status.StartsWith("The phrase could not be recognised: ", StringComparison.Ordinal), "an unexpected failure is shown too");
        Assert.EndsWith("The disk is full.", setup.Model.Status);
        await setup.Model.StopAsync();
    }

    [Fact]
    public async Task PastingInsteadOfTypingAndNoSpaceAfterChineseOrJapanese()
    {
        using var setup = Make();
        setup.Model.SelectedMethod = "Paste the words";
        setup.Speech.Read = (number, _, _) => Task.FromResult(number == 1 ? "Pasted." : "你好。");
        await setup.Model.StartAsync();
        setup.Microphone.Hear(Join(Phrase(), Phrase()));
        await UntilAsync(() => setup.Keys.Pasted.Count == 2, "both are pasted");
        await setup.Model.StopAsync();
        Assert.Empty(setup.Keys.Typed);
        Assert.Equal(["Pasted. ", "你好。"], setup.Keys.Pasted);
    }

    [Fact]
    public async Task AKeyboardThatRefusesTheWordsIsReportedAndNothingIsLost()
    {
        using var setup = Make();
        setup.Keys.Fail = new InvalidOperationException("Windows did not accept the keys.");
        await setup.Model.StartAsync();
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Model.Status == "Windows did not accept the keys.", "the refusal is shown");
        Assert.True(setup.Model.IsListening);
        setup.Keys.Fail = null;
        setup.Microphone.Hear(Phrase());
        await UntilAsync(() => setup.Keys.Typed.Count == 1, "typing works again");
        await setup.Model.StopAsync();
    }

    // ---- when dictation cannot start or stops by itself --------------------------------------------------------------------------

    [Fact]
    public async Task WithoutASpeechProgramDictationSaysWhyAndDoesNotListen()
    {
        using var setup = Make();
        setup.Engine = new DictationEngine(null, "No speech model is downloaded yet. Download one on the Models page.");
        setup.Model.RefreshAvailability();
        Assert.False(setup.Model.IsAvailable);
        Assert.True(setup.Model.HasNote);
        await setup.Model.StartAsync();
        Assert.False(setup.Model.IsListening);
        Assert.False(setup.Microphone.Open);
        Assert.Equal(setup.Engine.Note, setup.Model.Status);
        setup.Engine = new DictationEngine(setup.Speech);                                 // a model was downloaded
        setup.Model.RefreshAvailability();
        Assert.False(setup.Model.HasNote);
        await setup.Model.StartAsync();
        Assert.True(setup.Model.IsListening);
        await setup.Model.StopAsync();
    }

    [Fact]
    public async Task AMicrophoneThatCannotBeOpenedOrIsUnpluggedIsExplained()
    {
        using var setup = Make();
        setup.Microphone.CannotOpen = new MicrophoneException(WindowsMicrophone.Explain(4));
        await setup.Model.StartAsync();
        Assert.False(setup.Model.IsListening);
        Assert.StartsWith("The microphone is being used by another program.", setup.Model.Status);
        setup.Microphone.CannotOpen = null;

        await setup.Model.StartAsync();
        setup.Microphone.Hear(Join(Quiet(0.5), Tone(1.2, 0.08)));
        setup.Microphone.Unplug();
        await UntilAsync(() => !setup.Model.IsListening && setup.Model.Status.StartsWith("The microphone is being used", StringComparison.Ordinal), "dictation stops and says why");
        await UntilAsync(() => setup.Keys.Typed.Count == 1, "what was said before it went away is still typed");
    }

    [Fact]
    public async Task StartingTwiceOrStoppingWhenNothingRunsDoesNoHarm()
    {
        using var setup = Make();
        await setup.Model.StopAsync();
        await setup.Model.StartAsync();
        await setup.Model.StartAsync();
        Assert.True(setup.Model.IsListening);
        await setup.Model.ToggleListeningAsync();
        Assert.False(setup.Model.IsListening);
        await setup.Model.ToggleListeningAsync();
        Assert.True(setup.Model.IsListening);
        await setup.Model.StopAsync();
        await setup.Model.StopAsync();
        Assert.Empty(setup.Keys.Typed);
    }

    [Fact]
    public async Task TheLevelFollowsTheSoundWhileListeningAndIsZeroAfterwards()
    {
        using var setup = Make();
        await setup.Model.StartAsync();
        setup.Microphone.Hear(Quiet(0.5));
        setup.Model.Tick();
        Assert.InRange(setup.Model.Level, 0, 10);
        setup.Microphone.Hear(Tone(0.3, 0.1));
        setup.Model.Tick();
        Assert.InRange(setup.Model.Level, 30, 100);
        await setup.Model.StopAsync();
        Assert.Equal(0, setup.Model.Level);
    }

    // ---- the little window, the keys and what is remembered ------------------------------------------------------------------------

    [Fact]
    public async Task TheLittleWindowAppearsWithItsKeysAndGoingAwayStopsDictation()
    {
        using var setup = Make();
        setup.Model.ShowOverlay();
        Assert.True(setup.Model.IsOverlayVisible);
        Assert.Equal(1, setup.Screen.Shown);
        Assert.Equal(["Ctrl+Alt+Space"], setup.Hotkeys.Registrations.Select(item => item.Combo.Id));
        Assert.Equal("Hide the dictation window", setup.Model.OverlayButtonLabel);

        await setup.Model.ToggleListeningAsync();                                         // the hotkey, or the button of the window
        Assert.True(setup.Model.IsListening);
        await setup.Model.HideOverlayAsync();
        Assert.False(setup.Model.IsListening);
        Assert.False(setup.Model.IsOverlayVisible);
        Assert.Equal(1, setup.Screen.Hidden);
        Assert.False(setup.Hotkeys.Registered.ContainsKey(DictationViewModel.HotkeyAction));
        Assert.Equal("Show the dictation window", setup.Model.OverlayButtonLabel);
    }

    [Fact]
    public async Task KeysThatAnotherProgramUsesAreReportedAndNewKeysTakeOverAtOnce()
    {
        using var setup = Make();
        setup.Hotkeys.RegisterResult = false;
        setup.Model.ShowOverlay();
        Assert.True(setup.Model.HasHotkeyNote);
        Assert.Equal("The keys Ctrl+Alt+Space are used by another program. Choose other keys.", setup.Model.HotkeyNote);
        setup.Hotkeys.RegisterResult = true;
        FakeHotkeys.Choose(setup.Model.Keybind, new KeyCombo(0, 0x78));                       // F9
        Assert.False(setup.Model.HasHotkeyNote);
        Assert.Equal(["Ctrl+Alt+Space", "F9"], setup.Hotkeys.Registrations.Select(item => item.Combo.Id));
        Assert.Equal("Press the button or F9 to start dictating.", setup.Model.Status);
        await setup.Model.HideOverlayAsync();
    }

    [Fact]
    public void WhatWasChosenIsRememberedAndAWindowLeftOffScreenComesBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        using (var first = Make(root))
        {
            first.Model.SelectedLanguage = "hu";
            FakeHotkeys.Choose(first.Model.Keybind, new KeyCombo(KeyCombo.Control | KeyCombo.Alt, 0x44));
            first.Model.SelectedMethod = "Paste the words";
            first.Model.SelectedDevice = first.Model.Devices.First(device => device.Id == 1);
            first.Model.RememberPosition(120.5, 340);
            first.Model.Dispose();
            var again = Build(first);
            Assert.Equal("hu", again.SelectedLanguage);
            Assert.Equal("Ctrl+Alt+D", again.Keybind.Current.Id);
            Assert.Equal("Paste the words", again.SelectedMethod);
            Assert.Equal(1, again.SelectedDevice!.Id);
            Assert.Equal((120.5, 340d), again.Position);
        }
        var store = new DictationSettingsStore(root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{\"language\":\"\",\"hotkey\":\"nonsense\",\"method\":\"shout\",\"microphone\":-1}");
        var cleaned = store.Load();
        Assert.Equal("auto", cleaned.Language);
        Assert.Equal("Ctrl+Alt+Space", cleaned.Hotkey);
        File.WriteAllText(store.FilePath, "{\"hotkey\":\"ctrl-alt-d\"}");                       // the id an earlier version saved becomes the keys it stood for
        Assert.Equal("Ctrl+Alt+D", store.Load().Hotkey);
        File.WriteAllText(store.FilePath, "{\"hotkey\":\"\"}");                                  // no keys is a choice too
        Assert.Equal("", store.Load().Hotkey);
        File.WriteAllText(store.FilePath, "{\"hotkey\":\"A\"}");                                 // a letter on its own cannot be a hotkey
        Assert.Equal("Ctrl+Alt+Space", store.Load().Hotkey);
        File.WriteAllText(store.FilePath, "{\"language\":\"\",\"hotkey\":\"nonsense\",\"method\":\"shout\",\"microphone\":-1}");
        Assert.Equal("type", cleaned.Method);
        Assert.Null(DictationSettings.Normalize(new DictationSettings(Left: double.NaN, Top: double.PositiveInfinity)).Left);
        File.WriteAllText(store.FilePath, "this is not json");
        Assert.Equal(new DictationSettings(), store.Load());
        TestCleanup.Delete(root);
    }

    [Fact]
    public async Task TheTextsAreSaidInTheInterfaceLanguageAfterItChanges()
    {
        var before = Loc.Instance.Language;
        try
        {
            using var setup = Make();
            Assert.Equal("Press the button or Ctrl+Alt+Space to start dictating.", setup.Model.Status);
            Loc.Instance.SetLanguage("de");
            setup.Model.RefreshTexts();
            Assert.Equal("Drücken Sie die Schaltfläche oder Ctrl+Alt+Space, um mit dem Diktieren zu beginnen.", setup.Model.Status);
            Assert.Equal("Diktierfenster anzeigen", setup.Model.OverlayButtonLabel);
            setup.Engine = new DictationEngine(null, Loc.Describe(LiveMessages.NoModel));
            setup.Model.RefreshAvailability();
            Assert.StartsWith("Es ist noch kein Sprachmodell heruntergeladen.", setup.Model.Note);
            await Task.CompletedTask;
        }
        finally { Loc.Instance.SetLanguage(before); }
    }

    // ---- the keyboard and the keys ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TextBecomesKeyEventsThatNeedNoKeyboardLayout()
    {
        var events = WindowsKeyboard.EventsFor("Hi é");
        Assert.Equal(8, events.Count);
        Assert.Equal(new KeyEvent(0, 'H', false), events[0]);
        Assert.Equal(new KeyEvent(0, 'H', true), events[1]);
        Assert.Equal('é', events[^2].Character);
        Assert.All(events.Where((_, i) => i % 2 == 1), key => Assert.True(key.Up));

        var emoji = WindowsKeyboard.EventsFor("😀");                                      // two UTF-16 units, each sent as a key of its own
        Assert.Equal(4, emoji.Count);
        Assert.Equal(["😀"[0], "😀"[1]], emoji.Where(key => !key.Up).Select(key => key.Character));
    }

    [Fact]
    public void LineBreaksAndTabsAreKeysAndOtherControlCharactersAreLeftOut()
    {
        var events = WindowsKeyboard.EventsFor("a\r\nb\tc\u0001d\n");
        var virtualKeys = events.Where(key => key.Virtual != 0 && !key.Up).Select(key => key.Virtual).ToArray();
        Assert.Equal([(ushort)0x0D, (ushort)0x09, (ushort)0x0D], virtualKeys);              // Enter for CRLF (once), Tab, Enter
        Assert.Equal("abcd", string.Concat(events.Where(key => key.Virtual == 0 && !key.Up).Select(key => key.Character)));
        Assert.Empty(WindowsKeyboard.EventsFor(""));
    }

    [Fact]
    public void TheStructureWindowsIsGivenHasTheSizeItExpects() =>
        Assert.Equal(Environment.Is64BitProcess ? 40 : 28, WindowsKeyboard.InputSize);

    // ---- the real thing ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A sentence spoken by the voice Windows has, through the microphone's place, the phrase detector, the real Whisper program and the model, to the keyboard's place. Only the microphone and the
    /// keyboard are stand-ins. It is skipped on a computer without the speech program, a model or a voice.
    /// </summary>
    [Fact]
    public async Task ASpokenSentenceIsReadByTheRealSpeechProgramAndTypedThroughTheWholeChain()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var host = App.App.CreateHost(root);
            var live = host.Services.GetRequiredService<LocalLiveRecognizer>();
            if (!live.IsReady) return;
            var repositoryConfig = Path.Combine(TranslationSources.RepositoryRoot(), "Config");
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            foreach (var name in new[] { "hardware-profile.json", "active-execution.json" })                // so that the graphics card is used, as it is in the program
                if (File.Exists(Path.Combine(repositoryConfig, name))) File.Copy(Path.Combine(repositoryConfig, name), Path.Combine(root, "Config", name));
            var speech = await TestSpeech.CreateAsync(host.Services.GetRequiredService<IProcessRunner>(), Path.Combine(root, "Setup"), default);
            if (speech is null) return;
            var file = await File.ReadAllBytesAsync(speech);
            var data = file.AsSpan().IndexOf("data"u8);
            var pcm = file[(data + 8)..];

            var room = new Room();
            var keys = new Keyboard();
            using var model = new DictationViewModel(room, () => new DictationEngine(live), keys, new DictationSettingsStore(root), action => action());
            model.SelectedLanguage = "auto";
            await model.StartAsync();
            room.Hear(Join(Quiet(0.6), pcm, Quiet(1.6)));
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline && !(string.Join("", keys.Typed).Contains("settings", StringComparison.OrdinalIgnoreCase) || model.Status.Contains("could not", StringComparison.OrdinalIgnoreCase)))
                await Task.Delay(100);
            await model.StopAsync();

            var typed = string.Join("", keys.Typed);
            Assert.True(typed.Length > 0, "nothing was typed; status: " + model.Status);
            Assert.Contains("Mockingbird", typed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("settings", typed, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(" ", typed);                                                       // every phrase ends with a space, ready for the next
            Assert.False(Directory.Exists(Path.Combine(root, "Temp", "Live")) && Directory.GetFiles(Path.Combine(root, "Temp", "Live")).Length > 0, "the temporary files of the phrases are removed");
        }
        finally { TestCleanup.Delete(root); }
    }
}
