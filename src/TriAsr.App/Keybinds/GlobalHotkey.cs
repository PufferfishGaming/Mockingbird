using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TriAsr.App;

/// <summary>
/// The keys that work in every program, for everything in this program that has some (dictation, notes). The windows provide the real one; tests use a stand-in.
/// </summary>
public interface IHotkeys
{
    /// <summary>Makes <paramref name="combo"/> call <paramref name="pressed"/> from now on. Whatever <paramref name="action"/> had before is let go of.</summary>
    /// <returns>False when another program already uses the keys.</returns>
    bool Register(string action, KeyCombo combo, Action pressed);

    /// <summary>Lets go of the keys of an action. The action still has them: they are not offered to anything else.</summary>
    void Unregister(string action);

    /// <summary>Says which keys an action has, whether or not they are switched on, so that the same keys are not given to two things. No keys: the action has none.</summary>
    void Claim(string action, KeyCombo combo);

    /// <summary>Whether the keys can be chosen for <paramref name="action"/>: no other thing in this program has them and no other program uses them.</summary>
    bool IsFree(KeyCombo combo, string action);

    /// <summary>
    /// Switches every hotkey of this program off until the result is disposed, so that the person can press them again to choose them anew: a hotkey that is on
    /// would take the key press away from the window that is waiting for it.
    /// </summary>
    IDisposable Pause();
}

/// <summary>
/// A hotkey that works in every program (<c>RegisterHotKey</c> on a window of its own that has no surface). It has to be created on the window's thread.
/// A key combination that another program already uses cannot be registered; <see cref="Register"/> then says so.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312, HotkeyId = 0x4D42;
    private const uint NoRepeat = 0x4000;
    private static readonly IntPtr MessageOnly = new(-3);

    private HwndSource? _source;
    private bool _registered;

    /// <summary>Raised on the thread that created the hotkey when the keys are pressed.</summary>
    public event Action? Pressed;

    public bool Register(KeyCombo combo)
    {
        Unregister();
        if (combo.IsNone) return false;
        _source ??= Create();
        _registered = RegisterHotKey(_source.Handle, HotkeyId, combo.Modifiers | NoRepeat, combo.Key);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is not null) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private HwndSource Create()
    {
        var source = new HwndSource(new HwndSourceParameters("MockingbirdHotkey") { ParentWindow = MessageOnly, Width = 0, Height = 0 });
        source.AddHook(OnMessage);
        return source;
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;
        handled = true;
        Pressed?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
}

/// <summary>The hotkeys of one window of the program, each on a window of its own that has no surface. Created on the window's thread.</summary>
public sealed class WpfHotkeys : IHotkeys, IDisposable
{
    private sealed record Entry(GlobalHotkey Hotkey, KeyCombo Combo);

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, KeyCombo> _claims = [];
    private int _paused;

    public bool Register(string action, KeyCombo combo, Action pressed)
    {
        Unregister(action);
        Claim(action, combo);
        if (combo.IsNone) return true;
        var hotkey = new GlobalHotkey();
        hotkey.Pressed += pressed;
        _entries[action] = new Entry(hotkey, combo);
        return _paused > 0 || hotkey.Register(combo);
    }

    public void Unregister(string action)
    {
        if (!_entries.Remove(action, out var entry)) return;
        entry.Hotkey.Dispose();
    }

    public void Claim(string action, KeyCombo combo)
    {
        if (combo.IsNone) _claims.Remove(action); else _claims[action] = combo;
    }

    public bool IsFree(KeyCombo combo, string action)
    {
        if (combo.IsNone) return true;
        if (_claims.Any(claim => claim.Key != action && claim.Value == combo)) return false;
        // Another program: try the keys on a window of their own and let go of them again at once. This program's own keys are off while the person chooses (see Pause).
        if (_paused == 0 && _entries.TryGetValue(action, out var own) && own.Combo == combo) return true;
        using var scratch = new GlobalHotkey();
        return scratch.Register(combo);
    }

    public IDisposable Pause()
    {
        _paused++;
        foreach (var entry in _entries.Values) entry.Hotkey.Unregister();
        return new Resume(this);
    }

    private sealed class Resume(WpfHotkeys owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--owner._paused > 0) return;
            foreach (var entry in owner._entries.Values) entry.Hotkey.Register(entry.Combo);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Hotkey.Dispose();
        _entries.Clear();
    }
}
