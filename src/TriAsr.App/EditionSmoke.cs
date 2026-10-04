using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace TriAsr.App;

/// <summary>The smoke run of the Server and Client editions: the window is rendered once in every interface language.</summary>
public static class EditionSmoke
{
    /// <param name="shell">The Server edition's model: each page of its sidebar is rendered too, and the window is left on its first page.</param>
    public static async Task<int> RunAsync(Window window, Action<string> chooseLanguage, string root, ShellViewModel? shell = null)
    {
        var output = Path.Combine(root, "renders");
        Directory.CreateDirectory(output);
        var pages = 0;
        if (shell is not null)
        {
            foreach (var page in shell.ServerNavigation)
            {
                shell.SelectedPage = page;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (window is ServerWindow server && server.NavigationList.ItemContainerGenerator.ContainerFromItem(page) is UIElement { IsVisible: false } && !page.Advanced)
                    throw new InvalidOperationException($"The page {page.Name} is not listed in the sidebar.");
                ShellSmoke.Capture(window, Path.Combine(output, $"{Edition.Label.ToLowerInvariant()}-page-{page.Name.ToLowerInvariant().Replace(' ', '-')}.png"), window.Width, window.Height, 1);
                pages++;
            }
            shell.SelectedPage = shell.ServerNavigation[0];
        }
        var count = 0;
        foreach (var language in Loc.Languages)
        {
            chooseLanguage(language.Code);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            ShellSmoke.Capture(window, Path.Combine(output, $"{Edition.Label.ToLowerInvariant()}-{language.Code}.png"), window.Width, window.Height, 1);
            count++;
        }
        chooseLanguage(Loc.English);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        // The middle of the page (the Server edition's host panel has its Links part there), and then the lower part, which does not fit in the window.
        foreach (var scroller in Scrollers(window).Where(item => item.ScrollableHeight > 0)) scroller.ScrollToVerticalOffset(scroller.ScrollableHeight * 0.4);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        ShellSmoke.Capture(window, Path.Combine(output, $"{Edition.Label.ToLowerInvariant()}-middle.png"), window.Width, window.Height, 1);
        foreach (var scroller in Scrollers(window).Where(item => item.ScrollableHeight > 0)) scroller.ScrollToEnd();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        ShellSmoke.Capture(window, Path.Combine(output, $"{Edition.Label.ToLowerInvariant()}-end.png"), window.Width, window.Height, 1);
        return count + 1 + pages;
    }

    internal static IEnumerable<System.Windows.Controls.ScrollViewer> Scrollers(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is System.Windows.Controls.ScrollViewer scroller) yield return scroller;
            foreach (var inner in Scrollers(child)) yield return inner;
        }
    }

    public static Task WriteReportAsync(string root, int renders) => File.WriteAllTextAsync(Path.Combine(root, "bootstrap-smoke.json"),
        JsonSerializer.Serialize(new { success = true, windowCreated = true, edition = Edition.Label, shellMatrix = renders,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), utc = DateTimeOffset.UtcNow }));
}
