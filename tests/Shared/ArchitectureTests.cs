using System.IO;
using System.Xml.Linq;

namespace TriAsr.Tests;

public sealed class ArchitectureTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TriAsr.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run tests from the repository build output.");
    }
    [Fact]
    public void ReferencedProductionProjectsRespectDependencyBoundaries()
    {
        var root = RepositoryRoot();
        var suite = typeof(ArchitectureTests).Assembly.GetName().Name!;
        var testProject = XDocument.Load(Path.Combine(root, "tests", suite, suite + ".csproj"));
        var visited = new HashSet<string>();
        void Validate(string name)
        {
            if (!visited.Add(name)) return;
            var project = XDocument.Load(Path.Combine(root, "src", name, name + ".csproj"));
            var references = project.Descendants("ProjectReference").Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")!.Value)).ToArray();
            if (name == "TriAsr.Domain") { Assert.Empty(references); Assert.Empty(project.Descendants("PackageReference")); }
            else if (name == "TriAsr.Application") Assert.All(references, reference => Assert.Equal("TriAsr.Domain", reference));
            else if (name == "TriAsr.Worker") Assert.All(references, reference => Assert.Contains(reference, new[] { "TriAsr.Engine.Canary", "TriAsr.Audio" }));
            else if (name != "TriAsr.App") Assert.All(references, reference => Assert.Contains(reference, new[] { "TriAsr.Domain", "TriAsr.Application", "TriAsr.Alignment" }));
            if (name != "TriAsr.App") Assert.Empty(project.Descendants("UseWPF"));
            foreach (var reference in references) Validate(reference);
        }
        foreach (var reference in testProject.Descendants("ProjectReference")) Validate(Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value));
        Assert.NotEmpty(visited);
    }
}
