using System.Windows;
using Microsoft.Win32;

namespace TriAsr.App;

/// <summary>The watch-folder card: Studio's (its own engines do the work) and the Client's (the connected server does).</summary>
public partial class WatchCard : System.Windows.Controls.UserControl
{
    public WatchCard() => InitializeComponent();

    private void ChooseFolderClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not IWatchFolderOwner owner) return;
        var dialog = new OpenFolderDialog { Title = Loc.T("Choose the folder to watch for new recordings"), InitialDirectory = System.IO.Directory.Exists(owner.WatchFolder) ? owner.WatchFolder : null };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) owner.SetWatchFolder(dialog.FolderName);
    }
}
