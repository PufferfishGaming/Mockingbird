using System.Windows;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>The question asked the first time a server shows its certificate (and again, louder, if it shows another).</summary>
public partial class TrustServerWindow : Window
{
    public TrustServerWindow(TrustRequest request)
    {
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);
        Message.Text = !request.Encrypted
            ? Loc.T("{0} at {1} does not encrypt its connection. Anything you send, including its password, can be read on the network. Connect only if you trust the network.", request.Name, request.Address.Authority)
            : request.Changed
                ? Loc.T("The identity of {0} at {1} is not the one you trusted before. It may have been reinstalled or given a new identity, or someone may be pretending to be it. Continue only if you know why.", request.Name, request.Address.Authority)
                : Loc.T("You are connecting to {0} at {1} for the first time. Before you continue, check that the fingerprint below is the same as the one shown on that server, in its Identity box.", request.Name, request.Address.Authority);
        if (request.Fingerprint is { } fingerprint) Fingerprint.Text = ServerIdentity.Format(fingerprint);
        else { FingerprintLabel.Visibility = Visibility.Collapsed; FingerprintBox.Visibility = Visibility.Collapsed; }
        if (request.Changed || !request.Encrypted) Message.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrorBrush");
    }

    private void OnTrust(object sender, RoutedEventArgs args) { DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs args) { DialogResult = false; }
}

/// <summary>The window's answers to the connection's questions.</summary>
public sealed class WpfServerDialogs(Func<Window?> owner) : IServerDialogs
{
    public Task<bool> ConfirmTrustAsync(TrustRequest request)
    {
        var window = new TrustServerWindow(request) { Owner = owner() };
        return Task.FromResult(window.ShowDialog() == true);
    }

    public Task<PasswordAnswer?> AskPasswordAsync(string serverName, bool wrongBefore)
    {
        var window = new PasswordWindow(serverName, wrongBefore) { Owner = owner() };
        return Task.FromResult(window.ShowDialog() == true ? new PasswordAnswer(window.Entered, window.Remember) : null);
    }
}
