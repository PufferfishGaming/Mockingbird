using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TriAsr.App;

/// <summary>
/// Choosing the keys of something by pressing them. The person presses <i>Change keys</i>, presses the combination they want (whatever they press is shown as they
/// go), and finishes with the <i>Done</i> button, which is clicked with the mouse: every key goes to the combination, so none of them can press a button. The
/// keys of the program that are already on (dictation, notes) are switched off while the person chooses, so that pressing one of them can be chosen too.
/// </summary>
public sealed partial class KeybindViewModel : ObservableObject
{
    private readonly KeyCombo _default;
    private readonly Func<KeyCombo, string?> _refuse;
    private readonly Action<bool> _capturing;
    private readonly Action<KeyCombo> _changed;
    private uint _held;
    private bool _holding;
    private string? _problem;

    /// <param name="current">The keys now; none is allowed.</param>
    /// <param name="defaultCombo">The keys <i>Reset</i> goes back to.</param>
    /// <param name="refuse">Why the keys cannot be chosen (another program, or another thing in this one, uses them), or null.</param>
    /// <param name="capturing">Told when the window begins (true) and ends (false) waiting for keys.</param>
    /// <param name="changed">Told when different keys were chosen, or none.</param>
    public KeybindViewModel(KeyCombo current, KeyCombo defaultCombo, Func<KeyCombo, string?> refuse, Action<bool> capturing, Action<KeyCombo> changed)
    {
        _current = current; _default = defaultCombo; _refuse = refuse; _capturing = capturing; _changed = changed;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentText), nameof(IsSet))] private KeyCombo _current;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CaptureText), nameof(CanFinish))] private KeyCombo _pending;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NotCapturing))] private bool _isCapturing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))] private string _message = "";

    public bool NotCapturing => !IsCapturing;
    public bool HasMessage => Message.Length > 0;
    public bool IsSet => !Current.IsNone;

    /// <summary>Whether there are usual keys to go back to (dictation has them; notes start with none).</summary>
    public bool HasDefault => !_default.IsNone;

    /// <summary>The keys now, or that there are none.</summary>
    public string CurrentText => Current.IsNone ? Loc.T("Not set") : Current.Label;

    /// <summary>What the waiting window shows: the keys that were pressed, the ones held down so far, or what to do.</summary>
    public string CaptureText => _holding && _held != 0 ? KeyCombo.ModifierText(_held) + "…" : Pending.IsNone ? Loc.T("Press the keys you want to use") : Pending.Label;

    /// <summary>The Done button works once a combination that can be used was pressed.</summary>
    public bool CanFinish => !Pending.IsNone && Pending.Problem == KeyProblem.None;

    // ---- choosing ---------------------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Begin()
    {
        if (IsCapturing) return;
        Pending = KeyCombo.None; _held = 0; _holding = false; _problem = null; Message = "";
        IsCapturing = true;
        OnPropertyChanged(nameof(CaptureText));
        _capturing(true);
    }

    /// <summary>A modifier key (Ctrl, Alt, Shift) is down: what is held is shown with dots after it, until the main key is pressed.</summary>
    public void Hold(uint modifiers)
    {
        if (!IsCapturing) return;
        _held = modifiers; _holding = modifiers != 0;
        OnPropertyChanged(nameof(CaptureText));
    }

    /// <summary>A key was let go of while some modifiers are still held. What is shown follows them only while the person is still reaching for the main key; once the main key was pressed, the keys that were pressed stay on show.</summary>
    public void Held(uint modifiers)
    {
        if (!IsCapturing || !_holding) return;
        _held = modifiers;
        OnPropertyChanged(nameof(CaptureText));
    }

    /// <summary>The modifier keys were let go of without a main key being pressed: the keys pressed before are shown again.</summary>
    public void Release()
    {
        if (!IsCapturing) return;
        _held = 0; _holding = false;
        OnPropertyChanged(nameof(CaptureText));
    }

    /// <summary>The main key was pressed while <paramref name="modifiers"/> were held. A combination that cannot be used is shown with the reason at once.</summary>
    public void Press(uint key, uint modifiers)
    {
        if (!IsCapturing || !KeyCombo.CanBeKey(key)) return;
        _held = modifiers; _holding = false;
        Pending = new KeyCombo(modifiers, key);
        _problem = null;
        Message = ProblemText(Pending) ?? "";
        OnPropertyChanged(nameof(CaptureText));
    }

    private static string? ProblemText(KeyCombo combo) => combo.Problem switch
    {
        KeyProblem.NeedsModifier => Loc.T("Hold Ctrl or Alt while you press the key, or use a function key such as F9 on its own."),
        KeyProblem.CommonShortcut => Loc.T("Almost every program uses these keys (copying, pasting, closing and the like). Choose other keys."),
        KeyProblem.WindowsKey => Loc.T("Windows keeps the Windows key for its own shortcuts. Use Ctrl, Alt or Shift."),
        _ => null
    };

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Done()
    {
        if (!IsCapturing) return;
        var combo = Pending;
        if (ProblemText(combo) is { } problem) { Message = problem; return; }
        if (_refuse(combo) is { } refusal) { _problem = refusal; Message = refusal; return; }
        Current = combo;
        End();
        _changed(combo);
    }

    [RelayCommand]
    private void Cancel() { if (IsCapturing) End(); }

    /// <summary>Puts the usual keys in the window; <i>Done</i> then chooses them.</summary>
    [RelayCommand]
    private void Reset()
    {
        if (!IsCapturing) return;
        Pending = _default; _held = 0; _holding = false;
        Message = _default.IsNone ? "" : ProblemText(_default) ?? "";
        OnPropertyChanged(nameof(CaptureText));
    }

    /// <summary>No keys at all: the thing can only be started with its button.</summary>
    [RelayCommand]
    private void TurnOff()
    {
        if (!IsCapturing) return;
        Current = KeyCombo.None;
        End();
        _changed(KeyCombo.None);
    }

    private void End()
    {
        IsCapturing = false; Message = ""; _problem = null; _holding = false; _held = 0;
        _capturing(false);
    }

    partial void OnPendingChanged(KeyCombo value) => DoneCommand.NotifyCanExecuteChanged();

    /// <summary>Sets the keys from outside (the saved choice was read again) without telling the owner.</summary>
    public void Set(KeyCombo combo)
    {
        if (IsCapturing) End();
        Current = combo;
    }

    /// <summary>The interface language changed: the texts are built again.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(CaptureText));
        if (IsCapturing && _problem is null) Message = ProblemText(Pending) ?? "";
    }
}
