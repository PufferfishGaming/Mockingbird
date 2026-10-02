using System.Windows;
using Microsoft.Win32;

namespace TriAsr.App;

/// <summary>The window of the Server edition: a small page that shows what the server is doing. The models and the speech programs run on this computer.</summary>
public partial class ServerWindow : Window
{
    public ServerWindow(ShellViewModel viewModel)
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag); // numbers and dates follow the Windows regional settings
        InitializeComponent();
        DataContext = viewModel;
    }

    public ErrorBanner Errors => ErrorBanner;

    private void ShowPrivacyPolicy(object sender, RoutedEventArgs args) => PrivacyPolicyWindow.Show(this);

    private void ChooseStorageFolder(object sender, RoutedEventArgs args)
    {
        var forModels = (sender as System.Windows.Controls.Button)?.Tag?.ToString() == "models";
        var dialog = new OpenFolderDialog { Title = forModels ? Loc.T("Choose model repository") : Loc.T("Choose project and log folder") };
        if (dialog.ShowDialog(this) == true) ((ShellViewModel)DataContext).SetStorageLocation(dialog.FolderName, forModels);
    }

    /// <summary>Deletes a recording of the list (after asking): its transcript, edits and working files, and the copy that was uploaded.</summary>
    private async void DeleteProjectClick(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).DataContext is not TriAsr.Domain.TranscriptionJob job) return;
        if (!ProjectDialogs.ConfirmDelete(this, System.IO.Path.GetFileName(job.SourcePath), onServer: false)) return;
        await ((ShellViewModel)DataContext).DeleteProjectCommand.ExecuteAsync(job);
    }

    /// <summary>Closing the window stops the server. A recording that is being transcribed is only cancelled after the user has been asked.</summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        var vm = (ShellViewModel)DataContext;
        if ((vm.IsProcessing || vm.Host.IsBusy)
            && MessageBox.Show(this, Loc.T("A recording is being transcribed. Closing now stops the server and cancels it."), Loc.T("Close the server?"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        { args.Cancel = true; return; }
        if (vm.IsProcessing) vm.CancelCommand.Execute(null);
        vm.CancelModelCommand.Execute(null); vm.CancelBenchmarkCommand.Execute(null);
        vm.StopWatchingForExit();
        vm.Host.StopForExit();
        vm.Remote.Dispose();
        _ = vm.Servers.DisposeAsync().AsTask();
    }
}
