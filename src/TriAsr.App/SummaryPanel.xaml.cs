using System.Windows;

namespace TriAsr.App;

public partial class SummaryPanel : System.Windows.Controls.UserControl
{
    public SummaryPanel() => InitializeComponent();

    private async void SaveClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not SummaryViewModel summary) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = summary.SuggestedFileName, Filter = Loc.T("Markdown") + " (*.md)|*.md|" + Loc.T("Text") + " (*.txt)|*.txt", AddExtension = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await summary.SaveAsync(dialog.FileName);
    }
}
