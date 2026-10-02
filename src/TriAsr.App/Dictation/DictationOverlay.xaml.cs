using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace TriAsr.App;

/// <summary>
/// The little dictation window. It is a window that cannot be activated (<c>WS_EX_NOACTIVATE</c>): clicking its button does not move the keyboard away from the
/// program the person is typing into, which is the whole point. It stays above the others and can be dragged anywhere; where it is left is remembered.
/// </summary>
public partial class DictationOverlay : Window
{
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, WmMouseActivate = 0x0021, MaNoActivate = 3;

    private readonly DictationViewModel _model;

    public DictationOverlay(DictationViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
            HwndSource.FromHwnd(handle)?.AddHook(OnMessage);
        };
        Loaded += (_, _) => Place();
    }

    private static IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmMouseActivate) return IntPtr.Zero;
        handled = true;
        return new IntPtr(MaNoActivate);
    }

    /// <summary>Where it was left, if that is still on a screen; otherwise at the bottom of the main screen, in the middle.</summary>
    private void Place()
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_model.Position is { } saved && screen.Contains(new Point(saved.Left + 40, saved.Top + 20)))
        {
            Left = saved.Left; Top = saved.Top;
            return;
        }
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Bottom - ActualHeight - 24;
    }

    private void OnDrag(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is DependencyObject source)
            for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
                if (current is ButtonBase) return;
        try { DragMove(); }
        catch (InvalidOperationException) { return; }                // the button was released before the move began
        _model.RememberPosition(Left, Top);
    }

    private async void OnClose(object sender, RoutedEventArgs args) => await _model.HideOverlayAsync();

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}

/// <summary>Shows the little dictation window and listens for its hotkey, for one <see cref="DictationViewModel"/>. It is created on the window's thread.</summary>
public sealed class WpfDictationPresenter : IDictationPresenter, IDisposable
{
    private DictationOverlay? _overlay;
    private GlobalHotkey? _hotkey;
    private DictationViewModel? _model;

    public void ShowOverlay(DictationViewModel model)
    {
        _model = model;
        _overlay ??= new DictationOverlay(model);
        _overlay.Show();
    }

    public void HideOverlay()
    {
        _overlay?.Close();
        _overlay = null;
    }

    public bool RegisterHotkey(HotkeyChoice choice)
    {
        if (_hotkey is null)
        {
            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += async () => { if (_model is { } model) await model.ToggleListeningAsync(); };
        }
        return _hotkey.Register(choice);
    }

    public void UnregisterHotkey() => _hotkey?.Unregister();

    public void Dispose()
    {
        HideOverlay();
        _hotkey?.Dispose();
        _hotkey = null;
    }
}
