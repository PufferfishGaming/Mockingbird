using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TriAsr.App;

/// <summary>One combination of keys that starts and stops dictation from any program.</summary>
/// <param name="Label">The keys as they are written on the keyboard; it is not translated.</param>
/// <param name="Modifiers">The Windows modifier flags (Alt 1, Control 2, Shift 4).</param>
/// <param name="Key">The virtual-key code.</param>
public sealed record HotkeyChoice(string Id, string Label, uint Modifiers, uint Key)
{
    private const uint Alt = 1, Control = 2, Shift = 4;

    public static readonly IReadOnlyList<HotkeyChoice> All =
    [
        new("ctrl-alt-space", "Ctrl+Alt+Space", Control | Alt, 0x20),
        new("ctrl-shift-space", "Ctrl+Shift+Space", Control | Shift, 0x20),
        new("ctrl-alt-d", "Ctrl+Alt+D", Control | Alt, 0x44),
        new("f9", "F9", 0, 0x78),
        new("f8", "F8", 0, 0x77)
    ];

    public static HotkeyChoice Find(string id) => All.FirstOrDefault(choice => choice.Id == id) ?? All[0];
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

    public bool Register(HotkeyChoice choice)
    {
        Unregister();
        _source ??= Create();
        _registered = RegisterHotKey(_source.Handle, HotkeyId, choice.Modifiers | NoRepeat, choice.Key);
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
