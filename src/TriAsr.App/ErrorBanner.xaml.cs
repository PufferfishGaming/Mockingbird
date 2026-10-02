namespace TriAsr.App;

public partial class ErrorBanner : System.Windows.Controls.UserControl
{
    public ErrorBanner() => InitializeComponent();

    /// <summary>The alert itself, which a smoke run checks for its pulse.</summary>
    public System.Windows.Controls.Border Alert => ErrorAlert;
}
