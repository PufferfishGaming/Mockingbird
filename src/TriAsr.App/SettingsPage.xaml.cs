using System.Windows;
using Microsoft.Win32;

namespace TriAsr.App;

/// <summary>Appearance, privacy, updates, performance, transcription and storage (Studio and the Server window).</summary>
public partial class SettingsPage : System.Windows.Controls.UserControl
{
    public SettingsPage() => InitializeComponent();

    private void ShowPrivacyPolicy(object sender, RoutedEventArgs args) => PrivacyPolicyWindow.Show(Window.GetWindow(this));

    private void ChooseStorageFolder(object sender, RoutedEventArgs args)
    {
        var forModels = (sender as System.Windows.Controls.Button)?.Tag?.ToString() == "models";
        var dialog = new OpenFolderDialog { Title = forModels ? Loc.T("Choose model repository") : Loc.T("Choose project and log folder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ((ShellViewModel)DataContext).SetStorageLocation(dialog.FolderName, forModels);
    }
}
