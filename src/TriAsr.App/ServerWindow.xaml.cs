using System.Windows;

namespace TriAsr.App;

/// <summary>
/// The window of the Server edition: what the server is doing and its hosting on the first page, then the same setup pages as Studio's.
/// The models and the speech programs run on this computer.
/// </summary>
public partial class ServerWindow : Window
{
    public static readonly DependencyProperty IsNavigationCompactProperty = DependencyProperty.Register(
        nameof(IsNavigationCompact), typeof(bool), typeof(ServerWindow), new PropertyMetadata(false));

    /// <summary>A narrow window shows only the icons of its pages.</summary>
    public bool IsNavigationCompact
    {
        get => (bool)GetValue(IsNavigationCompactProperty);
        set => SetValue(IsNavigationCompactProperty, value);
    }

    public ServerWindow(ShellViewModel viewModel)
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag); // numbers and dates follow the Windows regional settings
        InitializeComponent();
        DataContext = viewModel;
    }

    public ErrorBanner Errors => ErrorBanner;

    /// <summary>The list of pages in the sidebar (the smoke test goes through them).</summary>
    public System.Windows.Controls.ListBox NavigationList => NavigationBar.NavigationList;

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        IsNavigationCompact = ActualWidth < 900;
        NavigationColumn.Width = new GridLength(IsNavigationCompact ? 72 : 230);
    }

    private async void CancelJobClick(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).DataContext is TriAsr.Domain.TranscriptionJob job) await ((ShellViewModel)DataContext).CancelJobAsync(job);
    }

    private async void ResumeJobClick(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).DataContext is TriAsr.Domain.TranscriptionJob job) await ((ShellViewModel)DataContext).ResumeJobAsync(job);
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
