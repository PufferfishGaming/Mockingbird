using System.IO;
using System.Windows;
using System.Windows.Media;
using TriAsr.Audio;

namespace TriAsr.App;

public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(nameof(SourcePath), typeof(string), typeof(WaveformView),
        new PropertyMetadata("", async (owner, args) => await ((WaveformView)owner).LoadAsync((string)args.NewValue)));
    public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    private float[] _peaks = [];
    private async Task LoadAsync(string path)
    {
        try
        {
            var peaks = await Task.Run(() =>
            {
                if (!File.Exists(path)) return Array.Empty<float>();
                var values = new float[1200];
                var info = WaveAudio.Inspect(path); var samplesPerBin = Math.Max(1, (info.SampleCount + values.Length - 1) / values.Length);
                long sample = 0;
                foreach (var chunk in WaveAudio.ReadChunks(path, 20)) foreach (var value in chunk)
                { var bin = (int)Math.Min(values.Length - 1, sample++ / samplesPerBin); values[bin] = Math.Max(values[bin], Math.Abs(value)); }
                return values;
            });
            if (SourcePath != path) return;
            _peaks = peaks; InvalidateVisual();
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { _peaks = []; InvalidateVisual(); }
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var brush = TryFindResource("AccentBrush") as Brush ?? SystemColors.HighlightBrush;
        var pen = new Pen(brush, Math.Max(1, ActualWidth / 1200));
        var center = ActualHeight / 2;
        for (var i = 0; i < _peaks.Length; i++)
        { var x = ActualWidth * i / _peaks.Length; var extent = Math.Max(1, _peaks[i] * center); drawing.DrawLine(pen, new(x, center - extent), new(x, center + extent)); }
    }
}
