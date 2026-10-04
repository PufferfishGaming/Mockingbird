using System.Windows;
using System.Windows.Media.Animation;

namespace TriAsr.App;

/// <summary>
/// The movement of the interface beyond what the control styles do (Themes/Controls.xaml): a page fades in when it is shown.
/// It follows Windows' "Animation effects" setting, and the smoke test turns it off (a picture of a page half faded in shows nothing).
/// </summary>
public static class Motion
{
    /// <summary>Whether things move at all.</summary>
    public static bool Enabled { get; set; } = SystemParameters.ClientAreaAnimation;

    public static readonly DependencyProperty FadeInProperty =
        DependencyProperty.RegisterAttached("FadeIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject element) => (bool)element.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject element, bool value) => element.SetValue(FadeInProperty, value);

    private static void OnFadeInChanged(DependencyObject element, DependencyPropertyChangedEventArgs change)
    {
        if (element is not UIElement page) return;
        page.IsVisibleChanged -= Shown;
        if (change.NewValue is true) page.IsVisibleChanged += Shown;
    }

    private static void Shown(object sender, DependencyPropertyChangedEventArgs change)
    {
        if (sender is not UIElement page || change.NewValue is not true) return;
        if (!Enabled) { page.BeginAnimation(UIElement.OpacityProperty, null); return; }
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
}
