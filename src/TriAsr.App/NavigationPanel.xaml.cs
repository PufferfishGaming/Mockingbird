using System.Windows;

namespace TriAsr.App;

/// <summary>The sidebar of Studio's window and of the Server window: the pages of that window, with the Advanced ones shown on demand.</summary>
public partial class NavigationPanel : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty PagesProperty = DependencyProperty.Register(
        nameof(Pages), typeof(IReadOnlyList<NavigationItem>), typeof(NavigationPanel), new PropertyMetadata(null));

    /// <summary>The pages this window lists.</summary>
    public IReadOnlyList<NavigationItem>? Pages
    {
        get => (IReadOnlyList<NavigationItem>?)GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    public NavigationPanel() => InitializeComponent();
}
