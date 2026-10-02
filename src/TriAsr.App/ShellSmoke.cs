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
        await CheckAdvancedPagesAsync(window, shell);
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
                var title = FindText(window, Loc.T(page.Title));
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
        count += await LanguageRendersAsync(window, shell, output);
        shell.ReportError("Download needs attention", "The download host is unavailable. Your downloaded models and partial files are retained. Retry when the connection is restored.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!shell.HasError || FindText(window, shell.ErrorTitle) is null) throw new InvalidOperationException("Error alert did not appear.");
        if (shell.PulseError && !window.ErrorAlert.HasAnimatedProperties) throw new InvalidOperationException("Error pulse did not start.");
        Capture(window, Path.Combine(output, "error-alert.png"), 1220, 900, 1);
        shell.DismissErrorCommand.Execute(null);
        if (shell.HasError) throw new InvalidOperationException("Error alert did not dismiss.");
        return count + 2;
    }

    /// <summary>Every language, live: the window changes language without a restart, and the pages with the most text are rendered for a look at the layout.</summary>
    private static async Task<int> LanguageRendersAsync(MainWindow window, ShellViewModel shell, string output)
    {
        var count = 0;
        shell.SelectedTheme = "Light";
        window.Width = 1220;
        // Two regions are open for the whole run, so that a language switch has something already shown to change.
        var regions = new[]
        {
            new ReviewRegion(new TriAsr.Domain.FinalRegion(0, 4000, "Guten Tag", "Guten Tag", "guten Tag", "uncertain", null, 0.5, null, false)),
            new ReviewRegion(new TriAsr.Domain.FinalRegion(4000, 9000, "Auf Wiedersehen", "Auf Wiedersehen", "Auf Wiedersehen", "llm-arbitrated", "B", 0.93))
        };
        foreach (var region in regions) shell.Regions.Add(region);
        shell.SelectedRegion = regions[0];
        foreach (var language in Loc.Languages)
        {
            shell.Language = language.Code;
            shell.SelectedPage = shell.Navigation.First(item => item.Name == "Review");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            foreach (var text in new[] { regions[0].Time, regions[0].Evidence, SourceText.Of("llm-arbitrated") })
                if (FindText(window, text) is null) throw new InvalidOperationException($"The open review does not show \"{text}\" in {language.Code}.");
            Capture(window, Path.Combine(output, $"lang-{language.Code}-Review-open.png"), 1220, 1100, 1);
            count++;
            foreach (var name in new[] { "New Transcription", "Models", "Benchmark", "Backends", "Settings" })
            {
                var page = shell.Navigation.First(item => item.Name == name);
                shell.SelectedPage = page;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (FindText(window, Loc.T(page.Title)) is null) throw new InvalidOperationException($"The title of {name} is not shown in {language.Code}.");
                if (FindText(window, Loc.T("Settings")) is null) throw new InvalidOperationException($"The sidebar is not shown in {language.Code}.");
                if (name == "New Transcription" && FindText(window, Loc.T("Select file")) is null) throw new InvalidOperationException($"The buttons are not shown in {language.Code}.");
                Capture(window, Path.Combine(output, $"lang-{language.Code}-{name.Replace(' ', '-')}.png"), 1220, 1100, 1);
                count++;
            }
        }
        foreach (var region in regions) shell.Regions.Remove(region);
        // The window that asks for the language on the first start, at the height it really takes.
        var chooser = new LanguageChoiceWindow(Loc.Detect()) { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        chooser.Show();
        await chooser.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(chooser, Path.Combine(output, "language-choice.png"), 520, double.PositiveInfinity, 1);
        chooser.Close();
        count++;
        shell.Language = Loc.English;
        shell.SelectedPage = shell.Navigation[0];
        return count;
    }

    /// <summary>The sidebar lists only the main pages until "Advanced" is opened, but never hides the page that is open.</summary>
    private static async Task CheckAdvancedPagesAsync(MainWindow window, ShellViewModel shell)
    {
        async Task<bool> ListedAsync(NavigationItem page)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var container = (UIElement?)window.NavigationList.ItemContainerGenerator.ContainerFromItem(page)
                ?? throw new InvalidOperationException($"No sidebar entry for {page.Name}.");
            return container.Visibility == Visibility.Visible;
        }
        var advanced = shell.Navigation.Where(page => page.Advanced).ToArray();
        shell.ShowAdvanced = false;
        shell.SelectedPage = shell.Navigation[0];
        foreach (var page in shell.Navigation)
            if (await ListedAsync(page) == page.Advanced) throw new InvalidOperationException($"Sidebar entry {page.Name} has the wrong visibility while Advanced is closed.");
        shell.SelectedPage = advanced[0];
        if (!await ListedAsync(advanced[0])) throw new InvalidOperationException("The open advanced page disappeared from the sidebar.");
        shell.SelectedPage = shell.Navigation[0];
        shell.ShowAdvanced = true;
        foreach (var page in shell.Navigation)
            if (!await ListedAsync(page)) throw new InvalidOperationException($"Sidebar entry {page.Name} is hidden while Advanced is open.");
        shell.ShowAdvanced = false;
    }

    private static System.Windows.Controls.TextBlock? FindText(DependencyObject root, string text)
    {
        if (root is System.Windows.Controls.TextBlock block && block.Text == text) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindText(VisualTreeHelper.GetChild(root, i), text) is { } match) return match;
        return null;
    }

    public static void Capture(Window window, string path, double width, double height, double scale)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        if (double.IsPositiveInfinity(height)) height = content.DesiredSize.Height; // a window that sizes itself to its content: take all of it
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        width = content.ActualWidth;
        height = content.ActualHeight;
        // Paint the window's own background first: the content on its own has none (it would be transparent).
        var picture = new DrawingVisual();
        using (var drawing = picture.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(picture);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
