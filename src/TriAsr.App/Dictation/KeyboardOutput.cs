using System.Runtime.InteropServices;

namespace TriAsr.App;

/// <summary>Puts words where the cursor is, in whatever program has the keyboard.</summary>
public interface ITextOutput
{
    /// <summary>Types the text key by key, as if the person had typed it.</summary>
    void Type(string text);

    /// <summary>Puts the text on the clipboard and pastes it with Ctrl+V; the clipboard's own text is put back afterwards.</summary>
    void Paste(string text);
}

/// <summary>One press or release of a key, as it is sent to Windows.</summary>
/// <param name="Virtual">A virtual-key code, or 0 when <paramref name="Character"/> is sent as a character.</param>
/// <param name="Character">The UTF-16 character of a Unicode key event.</param>
public readonly record struct KeyEvent(ushort Virtual, char Character, bool Up);

/// <summary>
/// Types with <c>SendInput</c>. Characters are sent as Unicode events, which need no keyboard layout and work for every language; the Enter and Tab keys
/// are sent as keys, because many programs ignore a typed line break. A program running as administrator does not receive input from one that does not.
/// </summary>
public sealed class WindowsKeyboard : ITextOutput
{
    private const ushort VkReturn = 0x0D, VkTab = 0x09, VkControl = 0x11, VkV = 0x56;
    private const int BatchEvents = 256;

    /// <summary>The size of the structure Windows is given for every key event; it has to be exactly what Windows expects (40 bytes in a 64-bit program) or no key is accepted.</summary>
    public static int InputSize => Marshal.SizeOf<Native.Input>();

    /// <summary>The key events that type a text: down and up for every character (a character outside the basic plane is two UTF-16 units, each sent).</summary>
    public static IReadOnlyList<KeyEvent> EventsFor(string text)
    {
        var events = new List<KeyEvent>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r': if (i + 1 < text.Length && text[i + 1] == '\n') i++; goto case '\n';
                case '\n': events.Add(new(VkReturn, '\0', false)); events.Add(new(VkReturn, '\0', true)); break;
                case '\t': events.Add(new(VkTab, '\0', false)); events.Add(new(VkTab, '\0', true)); break;
                case < ' ': break;                                     // other control characters are not typed
                default: events.Add(new(0, c, false)); events.Add(new(0, c, true)); break;
            }
        }
        return events;
    }

    public void Type(string text)
    {
        if (text.Length == 0) return;
        Send(EventsFor(text));
    }

    public void Paste(string text)
    {
        if (text.Length == 0) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        string? previous = null;
        var hadText = false;
        void Set()
        {
            try { hadText = System.Windows.Clipboard.ContainsText(); if (hadText) previous = System.Windows.Clipboard.GetText(); System.Windows.Clipboard.SetText(text); }
            catch (COMException) { /* the clipboard is held by another program: the paste below pastes what was there */ }
        }
        if (dispatcher is null) Set(); else dispatcher.Invoke(Set);
        Send([new(VkControl, '\0', false), new(VkV, '\0', false), new(VkV, '\0', true), new(VkControl, '\0', true)]);
        // The program reads the clipboard a moment after the key press, so the old text comes back only after a short wait.
        Thread.Sleep(250);
        void Restore()
        {
            try { if (hadText && previous is not null) System.Windows.Clipboard.SetText(previous); else if (!hadText) System.Windows.Clipboard.Clear(); }
            catch (COMException) { }
        }
        if (dispatcher is null) Restore(); else dispatcher.Invoke(Restore);
    }

    private static void Send(IReadOnlyList<KeyEvent> events)
    {
        for (var start = 0; start < events.Count; start += BatchEvents)
        {
            var batch = events.Skip(start).Take(BatchEvents).Select(Native.ToInput).ToArray();
            var sent = Native.SendInput((uint)batch.Length, batch, Marshal.SizeOf<Native.Input>());
            if (sent != batch.Length) throw new InvalidOperationException(Loc.T("Windows did not accept the keys. The program that has the keyboard may be running as administrator."));
        }
    }

    internal static class Native
    {
        private const uint InputKeyboard = 1, KeyUp = 0x0002, Unicode = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { public uint Type; public InputUnion Data; }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

        internal static Input ToInput(KeyEvent key) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = key.Virtual != 0
                    ? new KeyboardInput { VirtualKey = key.Virtual, Flags = key.Up ? KeyUp : 0 }
                    : new KeyboardInput { ScanCode = key.Character, Flags = Unicode | (key.Up ? KeyUp : 0) }
            }
        };

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, Input[] inputs, int size);
    }
}
