using System.Windows;

namespace TriAsr.App;

/// <summary>Asks which language the interface should use, the first time the app starts. The language Windows uses is listed first.</summary>
public partial class LanguageChoiceWindow : Window
{
    /// <summary>The language picked, or the suggested one when the window is closed without a choice.</summary>
    public string SelectedCode { get; private set; }

    public LanguageChoiceWindow(string suggested)
    {
        InitializeComponent();
        SelectedCode = Loc.IsSupported(suggested) ? suggested : Loc.English;
        Choices.ItemsSource = Loc.Languages.OrderBy(language => language.Code == SelectedCode ? 0 : 1).ToArray();
    }

    private void OnChoose(object sender, RoutedEventArgs args)
    {
        SelectedCode = (string)((FrameworkElement)sender).Tag;
        DialogResult = true;
    }
}
