using System.Windows;
using System.Windows.Input;

namespace TriAsr.App;

/// <summary>
/// Shows the keys of something and lets the person choose others by pressing them. While it waits, every key goes to it (none is left to press a button with),
/// and its buttons cannot take the keyboard: <i>Done</i> is clicked with the mouse.
/// </summary>
public partial class KeybindBox : System.Windows.Controls.UserControl
{
    public KeybindBox() => InitializeComponent();

    private KeybindViewModel? Model => DataContext as KeybindViewModel;

    /// <summary>The key a key event is about: Alt and F10 arrive as "system" keys, and an input method may hide the key.</summary>
    public static Key RealKey(KeyEventArgs args) => args.Key switch
    {
        Key.System => args.SystemKey,
        Key.ImeProcessed => args.ImeProcessedKey,
        Key.DeadCharProcessed => args.DeadCharProcessedKey,
        _ => args.Key
    };

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        args.Handled = true;                                    // nothing else may use the key: not a button, not a menu
        if (args.IsRepeat || Model is not { } model) return;
        var key = (uint)KeyInterop.VirtualKeyFromKey(RealKey(args));
        var modifiers = (uint)Keyboard.Modifiers;
        if (KeyCombo.IsModifierKey(key)) model.Hold(modifiers);
        else model.Press(key, modifiers);
    }

    private void OnKeyUp(object sender, KeyEventArgs args)
    {
        args.Handled = true;
        if (Model is not { } model) return;
        var modifiers = (uint)Keyboard.Modifiers;
        if (modifiers == 0) model.Release(); else model.Held(modifiers);
    }

    /// <summary>A click on the panel gives it the keyboard back after the person clicked somewhere else.</summary>
    private void OnPanelMouseDown(object sender, MouseButtonEventArgs args) => Panel.Focus();

    private void OnPanelVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is true) Dispatcher.BeginInvoke(new Action(() => { Panel.Focus(); Keyboard.Focus(Panel); OnFocusChanged(null, null); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnFocusChanged(object? sender, KeyboardFocusChangedEventArgs? args) =>
        FocusHint.Visibility = Panel.IsKeyboardFocused ? Visibility.Collapsed : Visibility.Visible;
}
