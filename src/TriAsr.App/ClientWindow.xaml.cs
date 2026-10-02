using System.Windows;

namespace TriAsr.App;

/// <summary>The window of the Client edition. It has no engines: it lists servers and shows the pages of the one it is connected to.</summary>
public partial class ClientWindow : Window
{
    public ClientWindow(ClientViewModel viewModel)
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag); // numbers and dates follow the Windows regional settings
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Dialogs = new WpfServerDialogs(() => this);
        viewModel.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(ClientViewModel.ShowServerPanel)) ApplyServerColumn(); };
        ApplyServerColumn();
    }

    public ErrorBanner Errors => ErrorBanner;

    private void ApplyServerColumn()
    {
        var show = DataContext is ClientViewModel { ShowServerPanel: true };
        ServerColumn.Width = new GridLength(show ? 320 : 0);
        ServerPanelHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPrivacyPolicy(object sender, RoutedEventArgs args) => PrivacyPolicyWindow.Show(this);

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs args) => ((ClientViewModel)DataContext).Close();
}
