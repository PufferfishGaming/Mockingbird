using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace TriAsr.App;

public sealed class ThemeManager : IDisposable
{
    private ResourceDictionary? _palette;
    private bool _listening;
    public string RequestedTheme { get; private set; } = "System";
    public string ActualTheme { get; private set; } = "Light";

    public void Apply(string requested)
    {
        RequestedTheme = requested is "System" or "Light" or "Dark" ? requested : "System";
        ActualTheme = RequestedTheme == "System" ? ReadSystemTheme() : RequestedTheme;
        var app = System.Windows.Application.Current;
        app.Dispatcher.VerifyAccess();
        var next = new ResourceDictionary
        {
            Source = new Uri($"/TriAsr.App;component/Themes/{ActualTheme}Theme.xaml", UriKind.Relative)
        };
        if (_palette is not null) app.Resources.MergedDictionaries.Remove(_palette);
        app.Resources.MergedDictionaries.Add(next);
        _palette = next;
        ApplyHighContrast();
        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnSystemChanged;
            SystemParameters.StaticPropertyChanged += OnParameterChanged;
            _listening = true;
        }
    }

    public static string ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? "Dark" : "Light";
        }
        catch (System.Security.SecurityException) { return "Light"; }
    }

    private void ApplyHighContrast()
    {
        if (!SystemParameters.HighContrast || _palette is null) return;
        foreach (var key in new[] { "WindowBackgroundBrush", "SurfaceBrush", "SurfaceSecondaryBrush", "SurfaceElevatedBrush" })
            _palette[key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush" })
            _palette[key] = SystemColors.WindowTextBrush;
        _palette["AccentBrush"] = SystemColors.HighlightBrush;
        _palette["AccentTextBrush"] = SystemColors.HighlightTextBrush;
        _palette["BorderBrush"] = SystemColors.WindowTextBrush;
    }

    private void OnSystemChanged(object sender, UserPreferenceChangedEventArgs args) => Refresh();
    private void OnParameterChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast)) Refresh();
    }
    private void Refresh()
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted) return;
        app.Dispatcher.BeginInvoke(() => Apply(RequestedTheme));
    }
    public void Dispose()
    {
        if (!_listening) return;
        SystemEvents.UserPreferenceChanged -= OnSystemChanged;
        SystemParameters.StaticPropertyChanged -= OnParameterChanged;
        _listening = false;
    }
}
