using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>Searching every transcript at once: the search itself, Studio's Projects page, a server's API, the Client and the web page.</summary>
public sealed class SearchTests
{
    private static FinalRegion Said(long start, string text, string? speaker = null) => new(start, start + 2000, text, text, "", "agreement", Speaker: speaker);

    private static FinalTranscript Meeting(Guid id) => new(id, "hu",
    [
        Said(0, "Jó reggelt mindenkinek.", "1"),
        Said(2000, "A határidő kötelező, ezt mindenki tudja.", "2"),
        Said(4000, "Rendben, a kötelező részt péntekig leadom.", "1")
    ], new Dictionary<string, string> { ["2"] = "Anna" });

    // ---- the search ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("A határidő kötelező", "kotelezo")] [InlineData("A határidő kötelező", "KÖTELEZŐ")] [InlineData("Die Übung", "ubung")]
    [InlineData("Weekly planning", "PLANNING")] [InlineData("zur  Sitzung .", "zur sitzung")]
    public void CapitalLettersAndAccentsDoNotMatter(string text, string query) => Assert.NotNull(ProjectSearch.Find(text, ProjectSearch.Normalize(query)));

    [Fact]
    public void AQueryIsTrimmedAndTooShortOneIsNoQuery()
    {
        Assert.Equal("határidő kötelező", ProjectSearch.Normalize("  határidő \t kötelező "));
        Assert.Equal("", ProjectSearch.Normalize("a"));
        Assert.Equal("", ProjectSearch.Normalize("   "));
        Assert.Null(ProjectSearch.Find("Willkommen", "Tag"));
        Assert.Equal((13, 12), ProjectSearch.Find("Willkommen   zur  Sitzung .", "zur sitzung"));      // cut from the text as it is written, its spaces kept
    }

    [Fact]
    public void AMatchingProjectShowsHowManyPassagesMatchAndTheFirstOfThemWithTheMatchPickedOut()
    {
        var id = Guid.NewGuid();
        var hit = ProjectSearch.Search(new SearchableProject(id, "standup.wav", DateTimeOffset.UtcNow, true), Meeting(id), "kotelezo")!;
        Assert.Equal(2, hit.Matches);
        Assert.False(hit.NameMatches);
        var first = hit.Passages[0];
        Assert.Equal((1, "A határidő ", "kötelező", ", ezt mindenki tudja."), (first.Index, first.Before, first.Match, first.After));   // the match as the transcript writes it
        Assert.Equal(("Anna", true), (first.Speaker, first.SpeakerNamed));
        Assert.Equal(("1", false), (hit.Passages[1].Speaker, hit.Passages[1].SpeakerNamed));
        Assert.Equal(2000, first.StartMs);

        var byName = ProjectSearch.Search(new SearchableProject(id, "Standup meeting.wav", DateTimeOffset.UtcNow, true), Meeting(id), "standup")!;
        Assert.True(byName.NameMatches);
        Assert.Equal(0, byName.Matches);
        Assert.Null(ProjectSearch.Search(new SearchableProject(id, "x.wav", DateTimeOffset.UtcNow, true), Meeting(id), "Willkommen"));
    }

    [Fact]
    public void ALongPassageIsShortenedAroundTheMatchAndOnlyTheFirstFewAreKept()
    {
        var id = Guid.NewGuid();
        var words = string.Join(" ", Enumerable.Range(1, 60).Select(i => "szó" + i));
        var transcript = new FinalTranscript(id, "hu", Enumerable.Range(0, 9).Select(i => Said(i * 2000, words + " CÉL " + words)).ToArray());
        var hit = ProjectSearch.Search(new SearchableProject(id, "hosszú.wav", DateTimeOffset.UtcNow, true), transcript, "cel")!;
        Assert.Equal(9, hit.Matches);
        Assert.Equal(ProjectSearch.PassagesPerProject, hit.Passages.Count);
        Assert.StartsWith("…", hit.Passages[0].Before);
        Assert.EndsWith("…", hit.Passages[0].After);
        Assert.True(hit.Passages[0].Before.Length < 80 && hit.Passages[0].After.Length < 80);
    }

    [Fact]
    public async Task TheNewestProjectsComeFirstAndOneThatCannotBeReadIsSkipped()
    {
        Guid older = Guid.NewGuid(), newer = Guid.NewGuid(), broken = Guid.NewGuid(), running = Guid.NewGuid();
        SearchableProject[] projects =
        [
            new(older, "a.wav", DateTimeOffset.UtcNow.AddDays(-2), true), new(newer, "b.wav", DateTimeOffset.UtcNow, true),
            new(broken, "c.wav", DateTimeOffset.UtcNow.AddDays(-1), true), new(running, "kotelezo.wav", DateTimeOffset.UtcNow.AddDays(-3), false)
        ];
        var read = new List<Guid>();
        var hits = await ProjectSearch.SearchAsync(projects, (id, _) =>
        {
            read.Add(id);
            return id == broken ? throw new IOException("gone") : Task.FromResult<FinalTranscript?>(Meeting(id));
        }, "kötelező", default);
        Assert.Equal([newer, older, running], hits.Select(hit => hit.JobId));      // the project still running matches by its name only
        Assert.DoesNotContain(running, read);                                       // it has no transcript to read
    }

    // ---- Studio's Projects page ---------------------------------------------------------------------------------------------------------

    private static async Task<Guid> SeedAsync(IHost host, string root, string name, FinalTranscript? transcript, int minutesAgo)
    {
        var id = transcript?.JobId ?? Guid.NewGuid();
        var folder = host.Services.GetRequiredService<IJobWorkspace>().DirectoryFor(id);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "normalized.wav"), new byte[1000]);
        if (transcript is not null) await File.WriteAllTextAsync(Path.Combine(folder, "final.json"), JsonSerializer.Serialize(transcript with { JobId = id }));
        var source = Path.Combine(root, "Music", name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, new byte[2000]);
        await host.Services.GetRequiredService<IJobRepository>().SaveAsync(new TranscriptionJob(id, source, "hu", transcript is null ? JobState.Failed : JobState.Complete, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)));
        return id;
    }

    private static async Task WaitForAsync(Func<bool> done, string because)
    {
        for (var i = 0; i < 300 && !done(); i++) await Task.Delay(20);
        Assert.True(done(), because);
    }

    [Fact]
    public Task StudioSearchesEveryTranscriptAndAPassageOpensTheTranscriptThere() => UiThread.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        using var host = App.App.CreateHost(root);
        try
        {
            await host.Services.GetRequiredService<IJobRepository>().InitializeAsync();
            var standup = await SeedAsync(host, root, "standup.wav", Meeting(Guid.NewGuid()), 5);
            await SeedAsync(host, root, "other.wav", new FinalTranscript(Guid.NewGuid(), "de", [Said(0, "Guten Tag.")]), 10);
            await SeedAsync(host, root, "kotelezo-hibas.wav", null, 20);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();

            shell.ProjectSearchText = "  KOTELEZO ";
            Assert.True(shell.HasProjectSearch);
            await WaitForAsync(() => shell.ProjectSearchStatus.StartsWith("Projects found"), "the search finishes");
            Assert.Equal("Projects found: 2", shell.ProjectSearchStatus);
            Assert.Equal(["standup.wav", "kotelezo-hibas.wav"], shell.SearchResults.Select(hit => hit.Name));   // a failed project by its name
            var hit = shell.SearchResults[0];
            Assert.Equal("Matching passages: 2", hit.MatchesText);
            Assert.Equal(("00:00:02", "Anna", "kötelező"), (hit.Passages[0].Time, hit.Passages[0].Speaker, hit.Passages[0].Match));
            Assert.Equal("Speaker 1", hit.Passages[1].Speaker);
            Assert.Equal("The name matches", shell.SearchResults[1].MatchesText);

            await shell.OpenSearchPassageCommand.ExecuteAsync(hit.Passages[1]);
            Assert.True(shell.IsReviewPage);
            Assert.Equal(standup, shell.SelectedJob!.Id);
            Assert.Same(shell.Regions[2], shell.SelectedRegion);                     // the passage's region, not the first

            shell.ProjectSearchText = "nincs ilyen szó";
            await WaitForAsync(() => shell.ProjectSearchStatus.StartsWith("Nothing found"), "nothing is found");
            Assert.Empty(shell.SearchResults);
            shell.ProjectSearchText = "x";
            Assert.False(shell.HasProjectSearch);                                    // too short: the project list is back
            await WaitForAsync(() => shell.ProjectSearchStatus == "", "the results go away");
        }
        finally { TestCleanup.Delete(root); }
    });

    // ---- a server's API, the Client and the web page --------------------------------------------------------------------------------------

    [Fact]
    public async Task AServerSearchesTheTranscriptsOfItsRecordings()
    {
        await using var api = await Harness.StartAsync(h => h.Transcript = Meeting);
        Assert.True(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("search").GetBoolean());
        var id = (await api.UploadAsync("?language=hu&name=standup.wav")).GetProperty("id").GetString()!;
        await api.WaitAsync(id);
        var found = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/search?q=" + Uri.EscapeDataString("kotelezo"))).RootElement;
        Assert.Equal("kotelezo", found.GetProperty("query").GetString());
        var hit = found.GetProperty("data")[0];
        Assert.Equal((id, "standup.wav", 2), (hit.GetProperty("jobId").GetString(), hit.GetProperty("name").GetString(), hit.GetProperty("matches").GetInt32()));
        var passage = hit.GetProperty("passages")[0];
        Assert.Equal(("kötelező", "Anna", 1), (passage.GetProperty("match").GetString(), passage.GetProperty("speaker").GetString(), passage.GetProperty("index").GetInt32()));
        using (var tooShort = await api.Client.GetAsync("/v1/search?q=k")) Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        using var client = new RemoteServerClient(api.Client.BaseAddress!, ApiTestData.Key, null);
        var result = await client.SearchAsync("KÖTELEZŐ", default);
        Assert.Equal(2, result.Data[0].Matches);
        Assert.Empty((await client.SearchAsync("nincs ilyen", default)).Data);
    }

    [Fact]
    public void ThePageSearchesThroughTheServerAndKeepsTheBoxWhileTheListIsRedrawn()
    {
        var script = File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "Web", "app.js"));
        Assert.Contains("getJson(\"/v1/search?q=\" + encodeURIComponent(query))", script);
        Assert.Contains("if (!ui.projectsBuilt) {", script);                                 // the box is made once, so typing is never cut off by the polling
        Assert.Contains("ui.search.hidden = !(state.info && state.info.search);", script);     // only a server that searches is asked
        Assert.Contains("async function openReviewAt(id, index)", script);
    }
}
