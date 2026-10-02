using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace TriAsr.App;

/// <summary>The pages of a connected server. The player and the review keys work as they do in Studio's Review page.</summary>
public partial class RemoteWorkspaceView : UserControl
{
    private bool _playing;
    private ReviewRegion? _lastSpoken;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public RemoteWorkspaceView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (DataContext is not RemoteWorkspaceViewModel model) return;
            if (Player.Source is null) { PlaybackFollower.Clear(model.Regions); _lastSpoken = null; return; }
            if (!SeekSlider.IsMouseCaptureWithin) SeekSlider.Value = Player.Position.TotalSeconds;
            PlayerTime.Text = TriAsr.Export.TranscriptExporter.Timestamp((long)Player.Position.TotalMilliseconds);
            var spoken = PlaybackFollower.Follow(model.Regions, (long)Player.Position.TotalMilliseconds);
            if (spoken is not null && spoken != _lastSpoken && _playing && FollowBox.IsChecked == true && !TranscriptEditor.IsKeyboardFocusWithin && model.ReviewItems.Contains(spoken))
            { model.SelectedRegion = spoken; RegionList.ScrollIntoView(spoken); }
            _lastSpoken = spoken;
        };
        Loaded += (_, _) =>
        {
            _timer.Start();
            if (DataContext is RemoteWorkspaceViewModel model && _dictationPresenter is null) { model.Dictation.Presenter = _dictationPresenter = new WpfOverlayPresenter(); model.Dictation.Hotkeys = _hotkeys = new WpfHotkeys(); model.Notes.Presenter = _notesPresenter = new WpfOverlayPresenter(); model.Notes.Hotkeys = _hotkeys; }
        };
        Unloaded += (_, _) => { _timer.Stop(); Player.Close(); _dictationPresenter?.Dispose(); _dictationPresenter = null; _notesPresenter?.Dispose(); _notesPresenter = null; _hotkeys?.Dispose(); _hotkeys = null; };
    }

    private RemoteWorkspaceViewModel Model => (RemoteWorkspaceViewModel)DataContext;
    private WpfOverlayPresenter? _dictationPresenter;
    private WpfHotkeys? _hotkeys;
    private WpfOverlayPresenter? _notesPresenter;

    private void SelectFileClick(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFileDialog { Filter = Loc.T("Audio / Video") + "|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.mp4;*.mkv;*.mov;*.webm|" + Loc.T("All files") + "|*.*" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) Model.SourcePath = picker.FileName;
    }

    private void OnFileDrop(object sender, DragEventArgs args)
    {
        if (args.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) Model.SourcePath = paths[0];
    }

    /// <summary>A click on a recording opens a finished one in Review; a click on its Delete button is not a click on the recording.</summary>
    private async void ProjectClick(object sender, MouseButtonEventArgs args)
    {
        var list = (ListBox)sender;
        if (args.OriginalSource is not DependencyObject source || list.ContainerFromElement(source) is not ListBoxItem item) return;
        if (ProjectDialogs.IsInsideButton(source, item) || item.DataContext is not RemoteJobRow row) return;
        await Model.OpenJobCommand.ExecuteAsync(row);
    }

    /// <summary>Enter opens the selected recording, Delete deletes it (after asking).</summary>
    private async void ProjectKeyDown(object sender, KeyEventArgs args)
    {
        if (args.OriginalSource is System.Windows.Controls.Primitives.ButtonBase || Model.SelectedJob is not { } row) return;
        if (args.Key == Key.Enter) { args.Handled = true; await Model.OpenJobCommand.ExecuteAsync(row); }
        else if (args.Key == Key.Delete && row.CanDelete) { args.Handled = true; await DeleteAsync(row); }
    }

    private async void DeleteProjectClick(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).DataContext is RemoteJobRow row) await DeleteAsync(row);
    }

    private async Task DeleteAsync(RemoteJobRow row)
    {
        if (!ProjectDialogs.ConfirmDelete(Window.GetWindow(this), row.Name, onServer: true)) return;
        await Model.DeleteJobCommand.ExecuteAsync(row);
    }

    private async void ExportClick(object sender, RoutedEventArgs args)
    {
        var picker = new SaveFileDialog { FileName = "transcript", Filter = Loc.T("Text") + "|*.txt|" + Loc.T("SubRip subtitles") + "|*.srt|WebVTT|*.vtt|Markdown|*.md|" + Loc.T("JSON with provenance") + "|*.json|" + Loc.T("CSV comparison") + "|*.csv|" + Loc.T("Word document") + "|*.docx" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await Model.ExportAsync(picker.FileName);
    }

    private void PlayRegionClick(object sender, RoutedEventArgs args)
    {
        if (Model.SelectedRegion is { } region) Player.Position = TimeSpan.FromMilliseconds(region.Original.StartMs);
        Player.Play();
        _playing = true;
    }

    private void PauseClick(object sender, RoutedEventArgs args) { Player.Pause(); _playing = false; }
    private void BackClick(object sender, RoutedEventArgs args) => Player.Position = TimeSpan.FromSeconds(Math.Max(0, Player.Position.TotalSeconds - 5));
    private void ForwardClick(object sender, RoutedEventArgs args) => Player.Position = TimeSpan.FromSeconds(Math.Min(SeekSlider.Maximum, Player.Position.TotalSeconds + 5));
    private void OnSeek(object sender, MouseButtonEventArgs args) => Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
    private void OnMediaOpened(object sender, RoutedEventArgs args) { if (Player.NaturalDuration.HasTimeSpan) SeekSlider.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds; }
    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs args) => Model.ReportPlaybackError(args.ErrorException.Message);

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Player is not null && SpeedPicker.SelectedItem is ComboBoxItem item)
            Player.SpeedRatio = double.Parse(item.Tag.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private void WaveformSeek(object sender, MouseButtonEventArgs args)
    {
        var waveform = (FrameworkElement)sender;
        Player.Position = TimeSpan.FromSeconds(Math.Clamp(args.GetPosition(waveform).X / Math.Max(1, waveform.ActualWidth), 0, 1) * SeekSlider.Maximum);
    }

    private void OnReviewKey(object sender, KeyEventArgs args)
    {
        var model = Model;
        if (!model.IsReviewTab || !model.HasReview || args.OriginalSource is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (args.Key)
        {
            case Key.Space: if (_playing) { Player.Pause(); _playing = false; } else { Player.Play(); _playing = true; } break;
            case Key.D1: model.UseWhisperCommand.Execute(null); break;
            case Key.D2: model.UseCanaryCommand.Execute(null); break;
            case Key.D3: model.UseAutomaticCommand.Execute(null); break;
            case Key.E: TranscriptEditor.Focus(); break;
            case Key.J: model.MoveReview(1); break;
            case Key.K: model.MoveReview(-1); break;
            default: return;
        }
        args.Handled = true;
    }
}
