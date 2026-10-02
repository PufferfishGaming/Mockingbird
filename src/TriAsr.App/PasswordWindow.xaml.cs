using System.Windows;

namespace TriAsr.App;

/// <summary>Asks for the password of a server.</summary>
public partial class PasswordWindow : Window
{
    public PasswordWindow(string serverName, bool wrongBefore)
    {
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);
        Message.Text = wrongBefore ? Loc.T("That password was not accepted. Enter the password of {0} again.", serverName) : Loc.T("{0} needs a password.", serverName);
        if (wrongBefore) Message.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrorBrush");
    }

    public string Entered => Entry.Password;
    public bool Remember => RememberBox.IsChecked == true;

    private void OnConnect(object sender, RoutedEventArgs args) { if (Entry.Password.Length > 0) DialogResult = true; }
}
