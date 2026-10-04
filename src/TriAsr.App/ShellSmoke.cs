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
        await KaraokeRendersAsync(window, shell, output);       // extra pictures for a look at the colours; not counted in the matrix
        DictationRenders(shell, output);
        SummaryRenders(shell, output);
        await NotesRendersAsync(window, shell, output);
        shell.ReportError("Download needs attention", "The download host is unavailable. Your downloaded models and partial files are retained. Retry when the connection is restored.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!shell.HasError || FindText(window, shell.ErrorTitle) is null) throw new InvalidOperationException("Error alert did not appear.");
        if (shell.PulseError && !window.ErrorBanner.Alert.HasAnimatedProperties) throw new InvalidOperationException("Error pulse did not start.");
        Capture(window, Path.Combine(output, "error-alert.png"), 1220, 900, 1);
        shell.DismissErrorCommand.Execute(null);
        if (shell.HasError) throw new InvalidOperationException("Error alert did not dismiss.");
        return count + 2;
    }

    /// <summary>
    /// The selected row while the recording is at its first word, in both themes: the word being said and the words already said must stand out from the
    /// words still to come, which have the accent colour of a selected row.
    /// </summary>
    private static async Task KaraokeRendersAsync(MainWindow window, ShellViewModel shell, string output)
    {
        var before = new ReviewRegion(new TriAsr.Domain.FinalRegion(0, 4000, "Guten Tag", "Guten Tag", "guten Tag", "agreement", null, 0.9, null, true));
        var region = new ReviewRegion(new TriAsr.Domain.FinalRegion(4000, 9000, "Auf Wiedersehen und bis bald", "Auf Wiedersehen und bis bald", "Auf Wiedersehen und bis bald", "agreement", null, 0.9, null, true));
        shell.Regions.Add(before); shell.Regions.Add(region);
        shell.SelectedRegion = region;
        foreach (var requested in new[] { "Dark", "Light" })
        {
            shell.SelectedTheme = requested;
            shell.SelectedPage = shell.Navigation.First(item => item.Name == "Review");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            region.PlayProgress = 0.3;      // set only now: the player's timer clears the marks while no recording is loaded
            Capture(window, Path.Combine(output, $"karaoke-{requested}.png"), 1220, 900, 1);
        }
        shell.Regions.Remove(before); shell.Regions.Remove(region);
        shell.SelectedTheme = "Light";
    }

    /// <summary>The summary panel of the Review page, unfolded with a summary in it, in both themes (the review's own pictures have no project open, so no panel).</summary>
    private static void SummaryRenders(ShellViewModel shell, string output)
    {
        var summary = new SummaryViewModel(action => action());
        summary.Open("weekly-meeting.wav", new TriAsr.Domain.MeetingSummary(
            "The team planned the launch of the new remote control. They chose a plastic case to keep the price at 25 euros and agreed to test the menu with older users first.",
            ["The selling price stays at 25 euros.", "A menu like a mobile phone's is easier than many small buttons.", "Older users need large print."],
            ["The case is made of plastic, not metal."],
            [new TriAsr.Domain.SummaryAction("Anna", "Send the cost figures to the team", "Friday"), new TriAsr.Domain.SummaryAction("Speaker 2", "Draw the first menu layout", "")],
            ["Should the remote light up when it is lost?"], "en", "gemma-3-12b-it-Q4_K_M.gguf", DateTimeOffset.Now),
            true, () => "", (_, _) => throw new InvalidOperationException());
        summary.IsOpen = true;
        var card = new Window { Content = new System.Windows.Controls.Grid { Children = { new SummaryPanel { DataContext = summary, Margin = new Thickness(24) } } } };
        try
        {
            foreach (var requested in new[] { "Dark", "Light" })
            {
                shell.SelectedTheme = requested;
                Capture(card, Path.Combine(output, $"summary-{requested}.png"), 960, double.PositiveInfinity, 1);
            }
        }
        finally { card.Close(); }
        shell.SelectedTheme = "Light";
    }

    /// <summary>The little dictation window in both themes. It is drawn but never shown, so that a smoke run does not put a window above the programs of the person running it.</summary>
    private static void DictationRenders(ShellViewModel shell, string output)
    {
        var overlay = new DictationOverlay(shell.Dictation);
        try
        {
            // The picture is taken of the window's content, whose own margin (room for the shadow) would be cut off: so the pill is drawn inside a plain grid.
            var pill = (FrameworkElement)overlay.Content;
            overlay.Content = null;
            pill.HorizontalAlignment = HorizontalAlignment.Left;
            overlay.Content = new System.Windows.Controls.Grid { Children = { pill } };
            foreach (var requested in new[] { "Dark", "Light" })
            {
                shell.SelectedTheme = requested;
                overlay.Background = new SolidColorBrush(requested == "Dark" ? Color.FromRgb(0x18, 0x18, 0x18) : Color.FromRgb(0xDD, 0xDD, 0xDD));   // the window is see-through; this stands for whatever is behind it
                Capture(overlay, Path.Combine(output, $"dictation-overlay-{requested}.png"), 440, double.PositiveInfinity, 2);
            }
        }
        finally { overlay.Close(); }
        // The card on the New transcription page, which is too far down that page to be in its picture.
        var card = new Window { Content = new System.Windows.Controls.Grid { Children = { new DictationCard { DataContext = shell.Dictation, Margin = new Thickness(24) } } } };
        try
        {
            foreach (var requested in new[] { "Dark", "Light" })
            {
                shell.SelectedTheme = requested;
                card.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
                Capture(card, Path.Combine(output, $"dictation-card-{requested}.png"), 960, double.PositiveInfinity, 1);
            }
        }
        finally { card.Close(); }
        shell.SelectedTheme = "Light";
    }

    /// <summary>The Notes page with two notes (one open), and its key-choosing panel, in both themes.</summary>
    private static async Task NotesRendersAsync(MainWindow window, ShellViewModel shell, string output)
    {
        var notes = shell.Notes;
        await notes.RefreshAsync();
        await notes.NewNoteAsync();
        notes.Title = "Weekly planning";
        notes.Text = "Monday: send the quarterly figures to the finance team.\nTuesday: review the contract with the supplier and call back about the delivery date.\nThursday: prepare the slides for the board meeting.";
        await notes.SaveNowAsync();
        await notes.NewNoteAsync();
        notes.Text = "Remember to book the train tickets for the conference in March, and ask whether the hotel has a quiet room.";
        await notes.SaveNowAsync();
        notes.SelectedNote = notes.Notes.First(row => row.DisplayTitle == "Weekly planning");
        await notes.Settled;
        shell.SelectedPage = shell.Navigation.First(item => item.Name == "Notes");
        window.Width = 1220;
        foreach (var requested in new[] { "Light", "Dark" })
        {
            shell.SelectedTheme = requested;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(output, $"notes-{requested}.png"), 1220, 1100, 1);
        }
        notes.Keybind.BeginCommand.Execute(null);
        notes.Keybind.Hold(KeyCombo.Control | KeyCombo.Alt);
        notes.Keybind.Press(0x4E, KeyCombo.Control | KeyCombo.Alt);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "notes-keys-Dark.png"), 1220, 1100, 1);
        notes.Keybind.CancelCommand.Execute(null);
        foreach (var row in notes.Notes.ToArray()) { notes.SelectedNote = row; await notes.Settled; await notes.DeleteOpenAsync(); }
        shell.SelectedTheme = "Light";
        shell.SelectedPage = shell.Navigation[0];
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
            regions[1].PlayProgress = 0.6;   // as if the recording were part-way through the second region: its words show how far (karaoke). Set just before the capture because the player's timer clears it when nothing is loaded.
            Capture(window, Path.Combine(output, $"lang-{language.Code}-Review-open.png"), 1220, 1100, 1);
            count++;
            foreach (var name in new[] { "New Transcription", "Notes", "Models", "Benchmark", "Backends", "Settings" })
            {
                var page = shell.Navigation.First(item => item.Name == name);
                shell.SelectedPage = page;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (FindText(window, Loc.T(page.Title)) is null) throw new InvalidOperationException($"The title of {name} is not shown in {language.Code}.");
                if (FindText(window, Loc.T("Settings")) is null) throw new InvalidOperationException($"The sidebar is not shown in {language.Code}.");
                if (FindText(window, Loc.T("Host a server")) is null) throw new InvalidOperationException($"The server panel is not shown in {language.Code}.");
                if (name == "New Transcription" && FindText(window, Loc.T("Select file")) is null) throw new InvalidOperationException($"The buttons are not shown in {language.Code}.");
                if (name == "New Transcription" && FindText(window, Loc.T("Dictation")) is null) throw new InvalidOperationException($"The dictation card is not shown in {language.Code}.");
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
        {
            if (page.Name == ShellViewModel.RemoteServerPage) { if (await ListedAsync(page)) throw new InvalidOperationException("The remote server page is listed without a connection."); continue; }
            if (await ListedAsync(page) == page.Advanced) throw new InvalidOperationException($"Sidebar entry {page.Name} has the wrong visibility while Advanced is closed.");
        }
        shell.SelectedPage = advanced[0];
        if (!await ListedAsync(advanced[0])) throw new InvalidOperationException("The open advanced page disappeared from the sidebar.");
        shell.SelectedPage = shell.Navigation[0];
        shell.ShowAdvanced = true;
        foreach (var page in shell.Navigation.Where(page => page.Name != ShellViewModel.RemoteServerPage))
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
            // The view box is the window's own area, so that a shadow or other effect that reaches beyond it does not stretch the picture.
            drawing.DrawRectangle(new VisualBrush(content) { Viewbox = new Rect(0, 0, width, height), ViewboxUnits = BrushMappingMode.Absolute }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(picture);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
