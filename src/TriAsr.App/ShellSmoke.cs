using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TriAsr.App;

public static class ShellSmoke
{
    public static async Task<int> RunAsync(MainWindow window, ShellViewModel shell, ThemeManager theme, SettingsStore settings, string root)
    {
        if (!window.Title.Contains(AppInfo.Version) || AppInfo.Version == "unknown")
            throw new InvalidOperationException("Application version is missing.");
        if (!AppInfo.PrivacyPolicy.Contains("## Terminal, exports and support"))
            throw new InvalidOperationException("Bundled privacy policy is incomplete.");
        var output = Path.Combine(root, "renders");
        Directory.CreateDirectory(output);
        var count = 0;
        foreach (var requested in new[] { "Light", "Dark", "System" })
        foreach (var scale in new[] { 1.0, 1.25, 1.5 })
        {
            shell.SelectedTheme = requested;
            if (theme.RequestedTheme != requested) throw new InvalidOperationException("Theme switch failed.");
            foreach (var page in shell.Navigation)
            {
                shell.SelectedPage = page;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (window.DataContext != shell) throw new InvalidOperationException("Navigation data context lost.");
                var title = FindText(window, page.Title);
                if (title is null) throw new InvalidOperationException($"Page title missing: {page.Name}");
            }
            shell.SelectedPage = shell.Navigation[0];
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(output, $"{requested}-{scale:0.00}.png"), 1160, 730, scale);
            count++;
        }
        shell.SelectedTheme = "Dark";
        shell.SelectedPage = shell.Navigation[^1];
        window.Width = 800;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!window.IsNavigationCompact) throw new InvalidOperationException("Narrow navigation did not collapse.");
        Capture(window, Path.Combine(output, "Dark-settings-narrow.png"), 800, 700, 1.0);
        await settings.SaveAsync(new("Dark", "Compact"));
        var loaded = await settings.LoadAsync();
        if (loaded.Theme != "Dark" || loaded.Density != "Compact") throw new InvalidOperationException("Preferences did not survive reload.");
        foreach (var page in new[] { "Models", "Benchmark", "Languages", "Backends", "Terminal" })
        foreach (var width in new[] { 800d, 1220d })
        {
            shell.SelectedPage = shell.Navigation.First(item => item.Name == page);
            window.Width = width;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(output, $"{page}-{width:0}.png"), width, 1000, 1);
            count++;
        }
        shell.ReportError("Download needs attention", "The download host is unavailable. Your downloaded models and partial files are retained. Retry when the connection is restored.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!shell.HasError || FindText(window, shell.ErrorTitle) is null) throw new InvalidOperationException("Error alert did not appear.");
        if (shell.PulseError && !window.ErrorAlert.HasAnimatedProperties) throw new InvalidOperationException("Error pulse did not start.");
        Capture(window, Path.Combine(output, "error-alert.png"), 1220, 900, 1);
        shell.DismissErrorCommand.Execute(null);
        if (shell.HasError) throw new InvalidOperationException("Error alert did not dismiss.");
        return count + 2;
    }

    private static System.Windows.Controls.TextBlock? FindText(DependencyObject root, string text)
    {
        if (root is System.Windows.Controls.TextBlock block && block.Text == text) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindText(VisualTreeHelper.GetChild(root, i), text) is { } match) return match;
        return null;
    }

    public static void Capture(MainWindow window, string path, double width, double height, double scale)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        width = content.ActualWidth;
        height = content.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
