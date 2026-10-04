using System.Windows;
using Microsoft.Win32;

namespace TriAsr.App;

/// <summary>Measuring the settings on this computer and keeping the fastest (Studio and the Server window).</summary>
public partial class TuningPage : System.Windows.Controls.UserControl
{
    public TuningPage() => InitializeComponent();

    private void SelectFileClick(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFileDialog { Filter = Loc.T("Audio / Video") + "|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.mp4;*.mkv;*.mov;*.webm|" + Loc.T("All files") + "|*.*" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) ((ShellViewModel)DataContext).SourcePath = picker.FileName;
    }
}
