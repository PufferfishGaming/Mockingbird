using System.Windows;
using Microsoft.Win32;

namespace TriAsr.App;

/// <summary>The GPU runtimes and the execution settings of the engines (Studio and the Server window).</summary>
public partial class BackendsPage : System.Windows.Controls.UserControl
{
    public BackendsPage() => InitializeComponent();

    private async void ImportRuntimeClick(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("Choose a compatible engine runtime folder with its DLL dependencies") };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await ((ShellViewModel)DataContext).ImportBackendAsync(picker.FolderName);
    }
}
