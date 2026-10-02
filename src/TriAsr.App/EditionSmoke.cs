using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace TriAsr.App;

/// <summary>The smoke run of the Server and Client editions: the window is rendered once in every interface language.</summary>
public static class EditionSmoke
{
    public static async Task<int> RunAsync(Window window, Action<string> chooseLanguage, string root)
    {
        var output = Path.Combine(root, "renders");
        Directory.CreateDirectory(output);
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
        return count + 1;
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
