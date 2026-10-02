using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TriAsr.App;

/// <summary>
/// The text of a region, with the words already said coloured and the word being said in bold, while the recording plays (ADR-0017).
/// <see cref="Progress"/> is how far into the region the recording is, 0 to 1; below zero (no recording playing here) the text is shown plainly.
/// </summary>
public sealed class KaraokeTextBlock : TextBlock
{
    public static readonly DependencyProperty KaraokeTextProperty = DependencyProperty.Register(nameof(KaraokeText), typeof(string), typeof(KaraokeTextBlock),
        new PropertyMetadata("", (d, _) => ((KaraokeTextBlock)d).Rebuild()));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(KaraokeTextBlock),
        new PropertyMetadata(-1.0, (d, _) => ((KaraokeTextBlock)d).Rebuild()));

    /// <summary>The words to show. (Not <c>Text</c>: the block builds its own pieces from it.)</summary>
    public string KaraokeText { get => (string)GetValue(KaraokeTextProperty); set => SetValue(KaraokeTextProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    private IReadOnlyList<KaraokePlan.WordSpan>? _words;
    private string _wordsOf = "";
    private int _shownWord = -2;
    private string _shownText = "";

    private void Rebuild()
    {
        var text = KaraokeText ?? "";
        if (Progress < 0)
        {
            if (_shownWord == -1 && _shownText == text) return;     // already plain
            Inlines.Clear(); Inlines.Add(new Run(text));
            _shownWord = -1; _shownText = text;
            return;
        }
        if (_words is null || _wordsOf != text) { _words = KaraokePlan.Words(text); _wordsOf = text; }
        var current = KaraokePlan.WordAt(_words, Progress);
        if (current == _shownWord && _shownText == text) return;     // the same word is still being said
        _shownWord = current; _shownText = text;
        Inlines.Clear();
        var said = (Brush)(TryFindResource("AccentBrush") ?? Brushes.DodgerBlue);
        var at = 0;
        for (var index = 0; index < _words.Count; index++)
        {
            var word = _words[index];
            if (word.Start > at) Inlines.Add(new Run(text[at..word.Start]));       // the space between words
            var run = new Run(text.Substring(word.Start, word.Length));
            if (index < current) run.Foreground = said;
            else if (index == current) { run.Foreground = said; run.FontWeight = FontWeights.Bold; }
            Inlines.Add(run);
            at = word.Start + word.Length;
        }
        if (at < text.Length) Inlines.Add(new Run(text[at..]));
    }
}
