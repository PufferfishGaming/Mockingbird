using System.Windows;
using Microsoft.Win32;

namespace TriAsr.App;

public partial class MainWindow : Window
{
    private void ShowPrivacyPolicy(object sender, RoutedEventArgs args)
    {
        var policy = new System.Windows.Controls.TextBox
        {
            Text = AppInfo.PrivacyPolicy,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Padding = new Thickness(22),
            FontSize = 14
        };
        policy.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        policy.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        new Window
        {
            Owner = this, Title = Loc.T("{0} · Privacy policy", AppInfo.Name),
            Width = 760, Height = 650, MinWidth = 460, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = policy
        }.ShowDialog();
    }

    private async void ImportRuntimeClick(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("Choose a compatible engine runtime folder with its DLL dependencies") };
        if (picker.ShowDialog(this) == true) await ((ShellViewModel)DataContext).ImportBackendAsync(picker.FolderName);
    }
    private void ChooseStorageFolder(object sender, RoutedEventArgs args)
    {
        var forModels = (sender as System.Windows.Controls.Button)?.Tag?.ToString() == "models";
        var dialog = new OpenFolderDialog { Title = forModels ? Loc.T("Choose model repository") : Loc.T("Choose project and log folder") };
        if (dialog.ShowDialog(this) == true) ((ShellViewModel)DataContext).SetStorageLocation(dialog.FolderName, forModels);
    }
    private bool _playing;
    public static readonly DependencyProperty IsNavigationCompactProperty = DependencyProperty.Register(
        nameof(IsNavigationCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsNavigationCompact
    {
        get => (bool)GetValue(IsNavigationCompactProperty);
        set => SetValue(IsNavigationCompactProperty, value);
    }
    public MainWindow(ShellViewModel viewModel)
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag); // numbers and dates follow the Windows regional settings
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Dialogs = new WpfServerDialogs(() => this);
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ShellViewModel.ShowServerPanel)) ApplyServerColumn();
            if (change.PropertyName == nameof(ShellViewModel.TerminalOutput) && viewModel.TerminalAutoScroll)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { TerminalOutputBox.UpdateLayout(); TerminalOutputBox.ScrollToEnd(); }));
        };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (Player.Source is null) return;
            if (!SeekSlider.IsMouseCaptureWithin) SeekSlider.Value = Player.Position.TotalSeconds;
            PlayerTime.Text = TriAsr.Export.TranscriptExporter.Timestamp((long)Player.Position.TotalMilliseconds);
        };
        timer.Start(); Closed += (_, _) => { timer.Stop(); Player.Close(); };
    }
    private void TerminalInputKeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        var vm = (ShellViewModel)DataContext;
        if (args.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        { if (vm.SubmitTerminalCommand.CanExecute(null)) vm.SubmitTerminalCommand.Execute(null); args.Handled = true; }
        else if (args.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        { vm.RecallTerminalCommand(args.Key == System.Windows.Input.Key.Up ? -1 : 1); args.Handled = true; }
    }
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        IsNavigationCompact = ActualWidth < 980;
        NavigationColumn.Width = new GridLength(IsNavigationCompact ? 72 : 230);
        ApplyServerColumn();
    }
    private void SelectFileClick(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFileDialog { Filter = Loc.T("Audio / Video") + "|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.mp4;*.mkv;*.mov;*.webm|" + Loc.T("All files") + "|*.*" };
        if (picker.ShowDialog(this) == true) ((ShellViewModel)DataContext).SourcePath = picker.FileName;
    }
    private void OnFileDrop(object sender, DragEventArgs args)
    {
        if (args.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            ((ShellViewModel)DataContext).SourcePath = paths[0];
    }
    private async void ExportClick(object sender, RoutedEventArgs args)
    {
        var picker = new SaveFileDialog { FileName = "transcript", Filter = Loc.T("Text") + "|*.txt|" + Loc.T("SubRip subtitles") + "|*.srt|WebVTT|*.vtt|Markdown|*.md|" + Loc.T("JSON with provenance") + "|*.json|" + Loc.T("CSV comparison") + "|*.csv|" + Loc.T("Word document") + "|*.docx" };
        if (picker.ShowDialog(this) == true) await ((ShellViewModel)DataContext).ExportAsync(picker.FileName);
    }
    private void PlayRegionClick(object sender, RoutedEventArgs args)
    {
        if (((ShellViewModel)DataContext).SelectedRegion is { } region) Player.Position = TimeSpan.FromMilliseconds(region.Original.StartMs);
        Player.Play();
        _playing = true;
    }
    private void PauseClick(object sender, RoutedEventArgs args) { Player.Pause(); _playing = false; }
    private void BackClick(object sender, RoutedEventArgs args) => Player.Position = TimeSpan.FromSeconds(Math.Max(0, Player.Position.TotalSeconds - 5));
    private void ForwardClick(object sender, RoutedEventArgs args) => Player.Position = TimeSpan.FromSeconds(Math.Min(SeekSlider.Maximum, Player.Position.TotalSeconds + 5));
    private void OnSeek(object sender, System.Windows.Input.MouseButtonEventArgs args) => Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
    private void OnMediaOpened(object sender, RoutedEventArgs args) { if (Player.NaturalDuration.HasTimeSpan) SeekSlider.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds; }
    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs args) => ((ShellViewModel)DataContext).ReportError(Loc.T("Audio playback failed"), args.ErrorException.Message);
    private void OnSpeedChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs args)
    {
        if (Player is not null && SpeedPicker.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            Player.SpeedRatio = double.Parse(item.Tag.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
    }
    /// <summary>The right-hand column is shown when it is switched on and the window is wide enough to leave room for the page beside it.</summary>
    private void ApplyServerColumn()
    {
        var show = DataContext is ShellViewModel { ShowServerPanel: true } && ActualWidth >= 900;
        ServerColumn.Width = new GridLength(show ? 320 : 0);
        ServerPanelHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        var vm = (ShellViewModel)DataContext;
        if (vm.IsProcessing) vm.CancelCommand.Execute(null);
        vm.CancelModelCommand.Execute(null); vm.CancelBenchmarkCommand.Execute(null);
        vm.StopWatchingForExit();
        vm.Host.StopForExit();
        vm.Remote.Dispose();
        _ = vm.Servers.DisposeAsync().AsTask();
    }
    private void ChooseWatchFolderClick(object sender, RoutedEventArgs args)
    {
        var vm = (ShellViewModel)DataContext;
        var dialog = new OpenFolderDialog { Title = Loc.T("Choose the folder to watch for new recordings"), InitialDirectory = System.IO.Directory.Exists(vm.WatchFolder) ? vm.WatchFolder : null };
        if (dialog.ShowDialog(this) == true) vm.SetWatchFolder(dialog.FolderName);
    }
    private void WaveformSeek(object sender, System.Windows.Input.MouseButtonEventArgs args)
    {
        var waveform = (FrameworkElement)sender;
        Player.Position = TimeSpan.FromSeconds(Math.Clamp(args.GetPosition(waveform).X / Math.Max(1, waveform.ActualWidth), 0, 1) * SeekSlider.Maximum);
    }
    private void OnReviewKey(object sender, System.Windows.Input.KeyEventArgs args)
    {
        var vm = (ShellViewModel)DataContext;
        if (!vm.IsReviewPage || args.OriginalSource is System.Windows.Controls.TextBox || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
        switch (args.Key)
        {
            case System.Windows.Input.Key.Space: if (_playing) { Player.Pause(); _playing = false; } else { Player.Play(); _playing = true; } break;
            case System.Windows.Input.Key.D1: vm.UseWhisperCommand.Execute(null); break;
            case System.Windows.Input.Key.D2: vm.UseCanaryCommand.Execute(null); break;
            case System.Windows.Input.Key.D3: vm.UseAutomaticCommand.Execute(null); break;
            case System.Windows.Input.Key.E: TranscriptEditor.Focus(); break;
            case System.Windows.Input.Key.J: vm.MoveReview(1); break;
            case System.Windows.Input.Key.K: vm.MoveReview(-1); break;
            default: return;
        }
        args.Handled = true;
    }
    public async Task VerifyAudioPlaybackAsync()
    {
        Player.Volume = 0; Player.Play();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && (!Player.NaturalDuration.HasTimeSpan || Player.Position.TotalMilliseconds < 200)) await Task.Delay(100);
        if (!Player.NaturalDuration.HasTimeSpan || Player.Position.TotalMilliseconds < 200) throw new InvalidOperationException("Native audio playback did not advance.");
        Player.Pause(); Player.Position = TimeSpan.Zero; Player.Volume = 1;
    }
}
