using System.Windows;

namespace TriAsr.App;

/// <summary>The privacy policy in the interface language, in a window of its own. Every edition has the same button.</summary>
public static class PrivacyPolicyWindow
{
    public static void Show(Window owner)
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
        policy.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        policy.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SurfaceBrush");
        new Window
        {
            Owner = owner, Title = Loc.T("{0} · Privacy policy", AppInfo.Name),
            Width = 760, Height = 650, MinWidth = 460, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = policy
        }.ShowDialog();
    }
}
