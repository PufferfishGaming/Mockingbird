using System.IO;
using System.Xml.Linq;

namespace TriAsr.Ui.SmokeTests;

/// <summary>Rules about the XAML that no other test notices until a window is on screen with real data in it.</summary>
public sealed class XamlRulesTests
{
    /// <summary>
    /// The value of a ProgressBar is bound two ways unless it is told otherwise, and a two-way binding to a property that cannot be set stops the program
    /// with an exception the moment such a bar is shown (the Client closed itself when its project list got its first recording). A bar only shows.
    /// </summary>
    [Fact]
    public void EveryProgressBarOnlyShowsItsValue()
    {
        var problems = new List<string>();
        foreach (var path in TranslationSources.XamlFiles())
            foreach (var bar in XDocument.Load(path).Descendants().Where(element => element.Name.LocalName == "ProgressBar"))
                if (bar.Attribute("Value")?.Value is { } value && value.StartsWith("{Binding", StringComparison.Ordinal) && !value.Contains("Mode=OneWay", StringComparison.Ordinal))
                    problems.Add($"{Path.GetFileName(path)}: Value=\"{value}\" needs Mode=OneWay");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
