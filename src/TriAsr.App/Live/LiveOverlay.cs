using System.ComponentModel;
using System.Windows.Input;

namespace TriAsr.App;

/// <summary>What the little window that floats above the other programs shows and does. Dictation and notes both use it.</summary>
public interface ILiveOverlay : INotifyPropertyChanged
{
    string Status { get; }

    /// <summary>The microphone's level, 0 to 100.</summary>
    double Level { get; }

    /// <summary>The words that were read last.</summary>
    string LastText { get; }

    /// <summary>The button with the microphone on it.</summary>
    ICommand ToggleListeningCommand { get; }

    /// <summary>Where the window was left on the screen, or null when it has not been moved.</summary>
    (double Left, double Top)? Position { get; }

    void RememberPosition(double left, double top);

    /// <summary>The window's own close button.</summary>
    Task HideOverlayAsync();
}

/// <summary>What shows the little window. The windows provide it; tests use a stand-in.</summary>
public interface IOverlayPresenter
{
    void ShowOverlay(ILiveOverlay model);
    void HideOverlay();
}
