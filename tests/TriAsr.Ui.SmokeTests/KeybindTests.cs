using TriAsr.App;

namespace TriAsr.Ui.SmokeTests;

/// <summary>Key combinations, and choosing them by pressing the keys (ADR: live dictation): the keys, what cannot be a hotkey, and the steps from "Change keys" to "Done".</summary>
public sealed class KeybindTests
{
    private const uint Ctrl = KeyCombo.Control, Alt = KeyCombo.Alt, Shift = KeyCombo.Shift, Win = KeyCombo.Windows;

    private static KeybindViewModel Make(KeyCombo current, FakeHotkeys? hotkeys = null, List<KeyCombo>? changes = null, string action = "dictation")
    {
        hotkeys ??= new FakeHotkeys();
        IDisposable? pause = null;
        return new KeybindViewModel(current, DictationViewModel.DefaultKeys,
            combo => hotkeys.IsFree(combo, action) ? null : Loc.T("The keys {0} are used by another program, or already have a use here. Choose other keys.", combo.Label),
            on => { if (on) pause = hotkeys.Pause(); else { pause?.Dispose(); pause = null; } },
            combo => changes?.Add(combo));
    }

    // ---- the keys ---------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Ctrl+Alt+Space", Ctrl | Alt, 0x20)]
    [InlineData("F9", 0u, 0x78)]
    [InlineData("Ctrl+Shift+PageDown", Ctrl | Shift, 0x22)]
    [InlineData("Alt+Num+", Alt, 0x6B)]
    [InlineData("Ctrl+Num5", Ctrl, 0x65)]
    [InlineData("Ctrl+Alt+7", Ctrl | Alt, 0x37)]
    [InlineData("Shift+F24", Shift, 0x87)]
    public void KeysAreWrittenInOneOrderAndReadBackToTheSameKeys(string text, uint modifiers, uint key)
    {
        Assert.True(KeyCombo.TryParse(text, out var combo));
        Assert.Equal(new KeyCombo(modifiers, key), combo);
        Assert.Equal(text, combo.Id);
        Assert.Equal(text, combo.Label);
    }

    [Fact]
    public void EveryKeyThatCanBeChosenSurvivesBeingSavedAndRead()
    {
        for (uint key = 1; key < 256; key++)
        {
            if (!KeyCombo.CanBeKey(key)) continue;
            var combo = new KeyCombo(Ctrl | Alt, key);
            Assert.True(KeyCombo.TryParse(combo.Id, out var again), "reading " + combo.Id);
            Assert.Equal(combo, again);
        }
        Assert.True(KeyCombo.CanBeKey(0x41) && KeyCombo.CanBeKey(0x20) && KeyCombo.CanBeKey(0x70) && KeyCombo.CanBeKey(0xBA));
        Assert.False(KeyCombo.CanBeKey(0x11) || KeyCombo.CanBeKey(0xA2) || KeyCombo.CanBeKey(0x5B) || KeyCombo.CanBeKey(0x14) || KeyCombo.CanBeKey(0));    // Ctrl, Left Ctrl, Win, Caps Lock, nothing
    }

    [Fact]
    public void TheKeysPunctuationIsSavedByCodeAndShownAsTheKeyboardPrintsIt()
    {
        var combo = new KeyCombo(Ctrl | Alt, 0xBA);
        Assert.Equal("Ctrl+Alt+OemBA", combo.Id);
        Assert.True(KeyCombo.TryParse("ctrl+alt+oemba", out var again));
        Assert.Equal(combo, again);
        Assert.StartsWith("Ctrl+Alt+", combo.Label);
        Assert.NotEqual("Ctrl+Alt+", combo.Label);
    }

    [Fact]
    public void TheOrderTheKeysAreWrittenInDoesNotMatterAndNoKeysIsAChoice()
    {
        Assert.True(KeyCombo.TryParse("space + alt + CTRL", out var combo));
        Assert.Equal(new KeyCombo(Ctrl | Alt, 0x20), combo);
        Assert.True(KeyCombo.TryParse("  ", out var none) && none.IsNone);
        Assert.Equal("", none.Id);
        Assert.Equal("", none.Label);
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Banana")]
    [InlineData("OemZZ")]
    [InlineData("+++")]
    public void TextThatIsNotAKeyCombinationIsRefused(string text)
    {
        Assert.False(KeyCombo.TryParse(text, out _));
    }

    [Fact]
    public void TheIdsOfAnEarlierVersionBecomeTheKeysTheyStoodFor()
    {
        Assert.True(KeyCombo.TryParse("ctrl-alt-space", out var space));
        Assert.Equal("Ctrl+Alt+Space", space.Id);
        Assert.Equal("Ctrl+Shift+Space", KeyCombo.ParseOr("ctrl-shift-space", default).Id);
        Assert.Equal("Ctrl+Alt+D", KeyCombo.ParseOr("ctrl-alt-d", default).Id);
        Assert.Equal("F9", KeyCombo.ParseOr("f9", default).Id);
        Assert.Equal("F8", KeyCombo.ParseOr("f8", default).Id);
        Assert.Equal(DictationViewModel.DefaultKeys, KeyCombo.ParseOr("nonsense", DictationViewModel.DefaultKeys));
        Assert.Equal("Ctrl+Alt+Space", KeyCombo.Normalize("nonsense", "Ctrl+Alt+Space"));
        Assert.Equal("", KeyCombo.Normalize("", "Ctrl+Alt+Space"));
    }

    [Theory]
    [InlineData(Ctrl | Alt, 0x20, KeyProblem.None)]
    [InlineData(0u, 0x78, KeyProblem.None)]                          // F9 on its own
    [InlineData(Shift, 0x7A, KeyProblem.None)]                       // Shift+F11
    [InlineData(Ctrl, 0x20, KeyProblem.None)]                        // Ctrl+Space
    [InlineData(Alt, 0x4E, KeyProblem.None)]                         // Alt+N
    [InlineData(0u, 0x41, KeyProblem.NeedsModifier)]                 // A on its own would stop typing an "a"
    [InlineData(Shift, 0x41, KeyProblem.NeedsModifier)]              // Shift+A types a capital
    [InlineData(0u, 0x20, KeyProblem.NeedsModifier)]
    [InlineData(Shift, 0x25, KeyProblem.NeedsModifier)]              // Shift+Left selects text
    [InlineData(Ctrl, 0x43, KeyProblem.CommonShortcut)]              // copy
    [InlineData(Ctrl, 0x56, KeyProblem.CommonShortcut)]              // paste
    [InlineData(Alt, 0x73, KeyProblem.CommonShortcut)]               // Alt+F4
    [InlineData(Alt, 0x09, KeyProblem.CommonShortcut)]               // Alt+Tab
    [InlineData(Ctrl | Win, 0x20, KeyProblem.WindowsKey)]
    [InlineData(Ctrl | Alt, 0u, KeyProblem.NoKey)]
    [InlineData(Ctrl | Alt, 0x11, KeyProblem.NoKey)]                 // Ctrl is not a main key
    public void WhatCannotBeAHotkeyIsToldAtOnce(uint modifiers, uint key, KeyProblem expected) =>
        Assert.Equal(expected, new KeyCombo(modifiers, key).Problem);

    [Fact]
    public void ModifiersAreShownInOneOrderWhileTheyAreHeld() =>
        Assert.Equal("Ctrl+Alt+Shift+", KeyCombo.ModifierText(Shift | Alt | Ctrl));

    // ---- choosing them by pressing them --------------------------------------------------------------------------------------------

    [Fact]
    public void KeysAreChosenByPressingThemAndClickingDone()
    {
        var hotkeys = new FakeHotkeys();
        var changes = new List<KeyCombo>();
        var keys = Make(DictationViewModel.DefaultKeys, hotkeys, changes);
        Assert.Equal("Ctrl+Alt+Space", keys.CurrentText);
        Assert.False(keys.IsCapturing);

        keys.BeginCommand.Execute(null);
        Assert.True(keys.IsCapturing);
        Assert.Equal(1, hotkeys.Paused);                                                    // the program's own hotkeys are off, so that pressing one can be chosen
        Assert.Equal("Press the keys you want to use", keys.CaptureText);
        Assert.False(keys.DoneCommand.CanExecute(null));                                    // nothing was pressed yet

        keys.Hold(Ctrl);
        Assert.Equal("Ctrl+…", keys.CaptureText);
        keys.Hold(Ctrl | Shift);
        Assert.Equal("Ctrl+Shift+…", keys.CaptureText);
        keys.Press(0x4B, Ctrl | Shift);                                                     // K
        Assert.Equal("Ctrl+Shift+K", keys.CaptureText);
        Assert.True(keys.DoneCommand.CanExecute(null));
        Assert.Empty(changes);                                                              // nothing changes until Done

        keys.DoneCommand.Execute(null);
        Assert.False(keys.IsCapturing);
        Assert.Equal("Ctrl+Shift+K", keys.CurrentText);
        Assert.Equal([new KeyCombo(Ctrl | Shift, 0x4B)], changes);
        Assert.Equal(0, hotkeys.Paused);                                                    // and the hotkeys are on again
    }

    [Fact]
    public void KeysThatCannotBeUsedSayWhyAndDoneStaysOff()
    {
        var keys = Make(DictationViewModel.DefaultKeys);
        keys.BeginCommand.Execute(null);
        keys.Press(0x41, 0);                                                                // A on its own
        Assert.StartsWith("Hold Ctrl or Alt", keys.Message);
        Assert.False(keys.DoneCommand.CanExecute(null));
        keys.Press(0x43, Ctrl);                                                             // copy
        Assert.StartsWith("Almost every program uses these keys", keys.Message);
        Assert.False(keys.DoneCommand.CanExecute(null));
        keys.Press(0x43, Ctrl | Alt);                                                       // a good one clears the message
        Assert.False(keys.HasMessage);
        Assert.True(keys.DoneCommand.CanExecute(null));
    }

    [Fact]
    public void TheKeysThatWerePressedStayOnShowWhileTheyAreLetGoOfOneByOne()
    {
        var keys = Make(DictationViewModel.DefaultKeys);
        keys.BeginCommand.Execute(null);
        keys.Hold(Ctrl);
        keys.Hold(Ctrl | Alt);
        keys.Held(Ctrl);                                                                    // Alt let go of before a main key: the display follows what is held
        Assert.Equal("Ctrl+…", keys.CaptureText);
        keys.Hold(Ctrl | Shift);
        keys.Press(0x4B, Ctrl | Shift);                                                     // K
        keys.Held(Ctrl | Shift);                                                            // K let go of, the modifiers still down
        Assert.Equal("Ctrl+Shift+K", keys.CaptureText);                                     // not "Ctrl+Shift+…"
        keys.Held(Ctrl);
        Assert.Equal("Ctrl+Shift+K", keys.CaptureText);
        keys.Release();                                                                     // everything let go of
        Assert.Equal("Ctrl+Shift+K", keys.CaptureText);
        Assert.True(keys.DoneCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(System.Windows.Input.Key.N, 0x4E, "N")]
    [InlineData(System.Windows.Input.Key.D5, 0x35, "5")]
    [InlineData(System.Windows.Input.Key.F9, 0x78, "F9")]
    [InlineData(System.Windows.Input.Key.F24, 0x87, "F24")]
    [InlineData(System.Windows.Input.Key.Space, 0x20, "Space")]
    [InlineData(System.Windows.Input.Key.Return, 0x0D, "Enter")]
    [InlineData(System.Windows.Input.Key.Escape, 0x1B, "Esc")]
    [InlineData(System.Windows.Input.Key.Next, 0x22, "PageDown")]
    [InlineData(System.Windows.Input.Key.Left, 0x25, "Left")]
    [InlineData(System.Windows.Input.Key.NumPad5, 0x65, "Num5")]
    [InlineData(System.Windows.Input.Key.Add, 0x6B, "Num+")]
    public void TheKeysWpfReportsAreTheCodesTheNamesAreMadeFor(System.Windows.Input.Key key, int code, string name)
    {
        var virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        Assert.Equal((uint)code, virtualKey);
        Assert.True(KeyCombo.CanBeKey(virtualKey));
        Assert.Equal(name, new KeyCombo(0, virtualKey).Label);
    }

    [Theory]
    [InlineData(System.Windows.Input.Key.LeftCtrl)] [InlineData(System.Windows.Input.Key.RightCtrl)] [InlineData(System.Windows.Input.Key.LeftAlt)] [InlineData(System.Windows.Input.Key.RightAlt)]
    [InlineData(System.Windows.Input.Key.LeftShift)] [InlineData(System.Windows.Input.Key.RightShift)] [InlineData(System.Windows.Input.Key.LWin)] [InlineData(System.Windows.Input.Key.RWin)]
    [InlineData(System.Windows.Input.Key.CapsLock)] [InlineData(System.Windows.Input.Key.NumLock)] [InlineData(System.Windows.Input.Key.Scroll)] [InlineData(System.Windows.Input.Key.None)]
    public void TheModifierKeysAndTheLockKeysAreNeverTheMainKey(System.Windows.Input.Key key)
    {
        var virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        Assert.False(KeyCombo.CanBeKey(virtualKey));
        Assert.Equal(key is System.Windows.Input.Key.CapsLock or System.Windows.Input.Key.NumLock or System.Windows.Input.Key.Scroll or System.Windows.Input.Key.None ? false : true, KeyCombo.IsModifierKey(virtualKey));
    }

    [Fact]
    public void TheModifierFlagsOfWpfAreTheFlagsWindowsWantsForAHotkey()
    {
        Assert.Equal(KeyCombo.Alt, (uint)System.Windows.Input.ModifierKeys.Alt);
        Assert.Equal(KeyCombo.Control, (uint)System.Windows.Input.ModifierKeys.Control);
        Assert.Equal(KeyCombo.Shift, (uint)System.Windows.Input.ModifierKeys.Shift);
        Assert.Equal(KeyCombo.Windows, (uint)System.Windows.Input.ModifierKeys.Windows);
    }
    [Fact]
    public void ModifierKeysAloneNeverBecomeTheMainKey()
    {
        var keys = Make(DictationViewModel.DefaultKeys);
        keys.BeginCommand.Execute(null);
        keys.Press(0x11, Ctrl);                                                             // the Ctrl key itself
        keys.Press(0x14, 0);                                                                // Caps Lock
        Assert.True(keys.Pending.IsNone);
        keys.Hold(Ctrl | Alt);
        keys.Release();                                                                     // everything let go of: the prompt is back
        Assert.Equal("Press the keys you want to use", keys.CaptureText);
    }

    [Fact]
    public void KeysAnotherProgramUsesAreRefusedAtDoneAndTheWindowKeepsWaiting()
    {
        var hotkeys = new FakeHotkeys();
        hotkeys.TakenByOthers.Add(new KeyCombo(Ctrl | Alt, 0x59));
        var changes = new List<KeyCombo>();
        var keys = Make(DictationViewModel.DefaultKeys, hotkeys, changes);
        keys.BeginCommand.Execute(null);
        keys.Press(0x59, Ctrl | Alt);
        keys.DoneCommand.Execute(null);
        Assert.True(keys.IsCapturing);
        Assert.Equal("The keys Ctrl+Alt+Y are used by another program, or already have a use here. Choose other keys.", keys.Message);
        Assert.Empty(changes);
        keys.Press(0x55, Ctrl | Alt);                                                       // Ctrl+Alt+U is free
        keys.DoneCommand.Execute(null);
        Assert.False(keys.IsCapturing);
        Assert.Equal("Ctrl+Alt+U", keys.CurrentText);
    }

    [Fact]
    public void KeysAnotherThingInTheProgramHasAreRefusedButTheOwnKeysCanBeChosenAgain()
    {
        var hotkeys = new FakeHotkeys();
        hotkeys.Claim("notes", new KeyCombo(Ctrl | Alt, 0x4E));
        hotkeys.Claim("dictation", DictationViewModel.DefaultKeys);
        var keys = Make(DictationViewModel.DefaultKeys, hotkeys);
        keys.BeginCommand.Execute(null);
        keys.Press(0x4E, Ctrl | Alt);
        keys.DoneCommand.Execute(null);
        Assert.True(keys.IsCapturing);                                                      // the keys of notes
        keys.Press(0x20, Ctrl | Alt);                                                       // the keys it has itself
        keys.DoneCommand.Execute(null);
        Assert.False(keys.IsCapturing);
    }

    [Fact]
    public void CancelKeepsTheOldKeysResetOffersTheUsualOnesAndTurnOffRemovesThem()
    {
        var hotkeys = new FakeHotkeys();
        var changes = new List<KeyCombo>();
        var keys = Make(new KeyCombo(0, 0x78), hotkeys, changes);                            // F9
        keys.BeginCommand.Execute(null);
        keys.Press(0x4B, Ctrl | Alt);
        keys.CancelCommand.Execute(null);
        Assert.False(keys.IsCapturing);
        Assert.Equal("F9", keys.CurrentText);
        Assert.Empty(changes);
        Assert.Equal(0, hotkeys.Paused);

        keys.BeginCommand.Execute(null);
        keys.ResetCommand.Execute(null);
        Assert.Equal("Ctrl+Alt+Space", keys.CaptureText);                                    // offered, not yet chosen
        Assert.Equal("F9", keys.CurrentText);
        keys.DoneCommand.Execute(null);
        Assert.Equal("Ctrl+Alt+Space", keys.CurrentText);

        keys.BeginCommand.Execute(null);
        keys.TurnOffCommand.Execute(null);
        Assert.False(keys.IsSet);
        Assert.Equal("Not set", keys.CurrentText);
        Assert.Equal([DictationViewModel.DefaultKeys, KeyCombo.None], changes);
        Assert.Equal(0, hotkeys.Paused);
    }

    [Fact]
    public void ThePromptAndTheKeysAreSaidInTheInterfaceLanguage()
    {
        var before = Loc.Instance.Language;
        try
        {
            var keys = Make(KeyCombo.None);
            Loc.Instance.SetLanguage("de");
            keys.RefreshTexts();
            Assert.Equal("Nicht festgelegt", keys.CurrentText);
            keys.BeginCommand.Execute(null);
            Assert.Equal("Drücken Sie die gewünschten Tasten", keys.CaptureText);
            keys.Press(0x41, 0);
            Assert.StartsWith("Halten Sie Strg oder Alt", keys.Message);
        }
        finally { Loc.Instance.SetLanguage(before); }
    }
}
