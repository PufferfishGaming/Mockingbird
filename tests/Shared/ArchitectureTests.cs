using System.IO;
using System.Text.RegularExpressions;
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
    private static IEnumerable<string> ProjectFiles(string root, string folder) =>
        Directory.GetFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
    private static string[] PackagesOf(string project) =>
        XDocument.Load(project).Descendants("PackageReference").Select(element => element.Attribute("Include")!.Value).Order().ToArray();

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

    /// <summary>
    /// Every NuGet package a production project uses is listed here. Adding a package is a deliberate decision
    /// (licence, size, attack surface, native binaries), so a new one fails this test until it is added on purpose.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedPackages = new()
    {
        ["TriAsr.App"] = ["CommunityToolkit.Mvvm", "Microsoft.Extensions.Hosting"],
        ["TriAsr.Application"] = ["Microsoft.Extensions.DependencyInjection.Abstractions"],
        ["TriAsr.Infrastructure"] = ["Microsoft.Extensions.Hosting", "Serilog.Extensions.Hosting", "Serilog.Formatting.Compact", "Serilog.Sinks.File"],
        ["TriAsr.Persistence"] = ["Microsoft.Data.Sqlite", "Microsoft.Extensions.DependencyInjection.Abstractions"],
    };

    [Fact]
    public void ProductionProjectsUseOnlyTheApprovedPackages()
    {
        var root = RepositoryRoot();
        var projects = ProjectFiles(root, "src").ToArray();
        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var allowed = AllowedPackages.GetValueOrDefault(name, []);
            Assert.True(PackagesOf(project).SequenceEqual(allowed.Order()),
                $"{name} packages are [{string.Join(", ", PackagesOf(project))}] but the approved list is [{string.Join(", ", allowed)}]. Adding a package needs a conscious update of this test.");
        }
    }

    [Fact]
    public void PackageVersionsAreManagedCentrallyAndEveryProjectHasALockFile()
    {
        var root = RepositoryRoot();
        foreach (var project in ProjectFiles(root, "src").Concat(ProjectFiles(root, "tests")))
        {
            var document = XDocument.Load(project);
            Assert.All(document.Descendants("PackageReference"), element =>
                Assert.True(element.Attribute("Version") is null && element.Attribute("VersionOverride") is null,
                    $"{Path.GetFileName(project)}: {element.Attribute("Include")?.Value} must take its version from Directory.Packages.props."));
            if (document.Descendants("PackageReference").Any() || document.Descendants("ProjectReference").Any())
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")), $"{Path.GetFileName(project)} has no packages.lock.json.");
        }
    }

    [Fact]
    public void OnlyTheAppAndTheWorkerAreExecutablesAndBothPinAnExplicitRuntime()
    {
        var root = RepositoryRoot();
        var executables = ProjectFiles(root, "src")
            .Where(project => XDocument.Load(project).Descendants("OutputType").Any(type => type.Value is "Exe" or "WinExe"))
            .Select(project => Path.GetFileNameWithoutExtension(project)!).Order().ToArray();
        Assert.Equal(["TriAsr.App", "TriAsr.Worker"], executables);
        foreach (var name in executables)
            Assert.True(XDocument.Load(Path.Combine(root, "src", name, name + ".csproj")).Descendants("RuntimeIdentifier").Any(),
                $"{name} must declare its RuntimeIdentifier so it is never an architecture-neutral executable.");
    }

    [Fact]
    public void ProductionProjectsNeverReferenceTestProjects()
    {
        var root = RepositoryRoot();
        foreach (var project in ProjectFiles(root, "src"))
            Assert.DoesNotContain(XDocument.Load(project).Descendants("ProjectReference"), element => element.Attribute("Include")!.Value.Contains("Tests", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex ForbiddenInPureLayers = new(
        @"\b(System\.Windows|PresentationFramework|System\.Net\.Http|System\.Net\.Sockets|System\.Diagnostics\.Process|Microsoft\.Data\.Sqlite|Microsoft\.Win32)\b", RegexOptions.Compiled);

    [Theory]
    [InlineData("TriAsr.Domain")]
    [InlineData("TriAsr.Alignment")]
    [InlineData("TriAsr.Fusion")]
    [InlineData("TriAsr.Application")]
    [InlineData("TriAsr.Export")]
    public void PureLayersUseNoUiNetworkProcessOrDatabaseApis(string project)
    {
        var root = RepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();
        Assert.NotEmpty(files);
        var offenders = files.SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line: line.Trim(), number: index + 1)))
            .Where(item => !item.line.StartsWith("//", StringComparison.Ordinal) && ForbiddenInPureLayers.IsMatch(item.line))
            .Select(item => $"{Path.GetFileName(item.path)}:{item.number}: {item.line}").ToArray();
        Assert.True(offenders.Length == 0, project + " uses an API that belongs to an outer layer:\n" + string.Join("\n", offenders));
    }
}
