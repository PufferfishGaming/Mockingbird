using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using TriAsr.App;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The web page of a server: served to a browser without a password, locked down by its policy, translated, and the API behind it unchanged.</summary>
public sealed class WebAppTests
{
    private static HttpRequestMessage Browser(string path) => new(HttpMethod.Get, path) { Headers = { { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" } } };

    private static string WebFolder() => Path.Combine(TranslationSources.AppFolder, "Web");

    [Fact]
    public async Task ABrowserGetsThePageWithoutAPasswordAndAProgramStillGetsTheTextOverview()
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };

        using var page = await anonymous.SendAsync(Browser("/"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<title>Mockingbird Client Webview</title>", html);
        Assert.Contains("const PRODUCT = \"Mockingbird Client Webview\"", await anonymous.GetStringAsync("/app.js"));
        Assert.Contains("<script src=\"/app.js\"></script>", html);
        Assert.Contains("<script src=\"/live.js\"></script>", html);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/app.css\">", html);
        Assert.DoesNotContain("<script>", html);   // nothing inline: the policy below allows only the script that comes from this server

        Assert.Contains("local transcription API", await anonymous.GetStringAsync("/"));          // curl, a client library: as before
        using var both = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { { "Accept", "application/json" } } };
        Assert.Contains("local transcription API", await (await anonymous.SendAsync(both)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EveryFileOfThePageComesWithAPolicyThatAllowsNothingButTheServerItself()
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        foreach (var (path, type) in new[] { ("/", "text/html"), ("/app.js", "text/javascript"), ("/live.js", "text/javascript"), ("/worklet.js", "text/javascript"), ("/app.css", "text/css"), ("/ui/strings.json?lang=hu", "application/json") })
        {
            using var response = await anonymous.SendAsync(Browser(path));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(WebApp.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        }
        Assert.Contains("default-src 'none'", WebApp.ContentSecurityPolicy);
        Assert.DoesNotContain("unsafe-inline", WebApp.ContentSecurityPolicy);
        Assert.DoesNotContain("unsafe-eval", WebApp.ContentSecurityPolicy);
        Assert.Contains("frame-ancestors 'none'", WebApp.ContentSecurityPolicy);
    }

    [Fact]
    public async Task ThePageOpensTheWayItShouldButEverythingItShowsStillNeedsThePassword()
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(Browser("/"))).StatusCode);
        foreach (var path in new[] { "/v1/server", "/v1/transcriptions", $"/v1/transcriptions/{Guid.NewGuid()}/review", $"/v1/transcriptions/{Guid.NewGuid()}/audio" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        // The page may not be posted to, and is not a way around the API's rules.
        using var post = await anonymous.PostAsync("/app.js", new StringContent("x"));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        using var unknown = await anonymous.SendAsync(Browser("/admin"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData("hu")] [InlineData("de")] [InlineData("es")] [InlineData("fr")]
    public async Task ThePageGetsEveryTextItUsesInTheLanguageAsked(string code)
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(await anonymous.GetStringAsync($"/ui/strings.json?lang={code}"))!;
        var table = Loc.Load(code);
        var script = File.ReadAllText(Path.Combine(WebFolder(), "app.js"));
        var used = Regex.Matches(script, "(?<![\\w.])t\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Select(match => TranslationSources.Unescape(match.Groups[1].Value)).Distinct().ToArray();
        Assert.True(used.Length > 40);
        foreach (var text in used)
        {
            Assert.True(strings.TryGetValue(text, out var translated) && translated.Length > 0, $"{code}: {text}");
            Assert.Equal(table[text], translated);
        }
        // The names the server sends in English, which the page translates in the same way: a language and a stage of the work.
        Assert.Equal(table["German"], strings["German"]);
        Assert.Equal(table["Preparing audio"], strings["Preparing audio"]);
        Assert.Equal(table["agreement"], strings["agreement"]);                    // how a region came about
        Assert.Equal(table[TriAsr.Fusion.TranscriptQuality.RepetitionWarning], strings[TriAsr.Fusion.TranscriptQuality.RepetitionWarning]);
        Assert.True(strings.Count < table.Count, "only what the page uses is sent, not the whole table");
    }

    [Fact]
    public async Task EnglishNeedsNoTranslationsAndAnUnknownLanguageIsRefused()
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        Assert.Equal("{}", await anonymous.GetStringAsync("/ui/strings.json?lang=en"));
        Assert.Equal("{}", await anonymous.GetStringAsync("/ui/strings.json"));
        using var refused = await anonymous.GetAsync("/ui/strings.json?lang=xx");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1:8642", true)] [InlineData("192.168.1.20:8642", true)] [InlineData("[::1]:8642", true)] [InlineData("[fe80::1]", true)]
    [InlineData("localhost:8642", true)] [InlineData("kitchen.localhost", true)] [InlineData(null, true)] [InlineData("", true)]
    [InlineData("evil.example", false)] [InlineData("evil.example:8642", false)] [InlineData("localhost.evil.example", false)] [InlineData("192.168.1.20.evil.example", false)]
    public void OnlyAnAddressLocalhostOrTheNameOfTheComputerIsAnExpectedHost(string? header, bool expected) => Assert.Equal(expected, WebApp.IsExpectedHost(header));

    [Fact]
    public void TheNameOfThisComputerIsExpectedWithOrWithoutItsDomainAndNothingThatMerelyStartsWithIt()
    {
        var machine = Environment.MachineName;
        Assert.True(WebApp.IsExpectedHost(machine));
        Assert.True(WebApp.IsExpectedHost(machine.ToLowerInvariant() + ":8642"));
        Assert.True(WebApp.IsExpectedHost(machine + ".lan"));
        Assert.False(WebApp.IsExpectedHost(machine + "x"));
        Assert.False(WebApp.IsExpectedHost("x" + machine));
    }

    [Fact]
    public async Task AServerWithoutAPasswordRefusesRequestsAddressedToAnotherNameAndAServerWithOneDoesNot()
    {
        await using var api = await Harness.StartAsync(configure: harness => harness.CurrentKey = "");
        using var other = new HttpClient { BaseAddress = api.Client.BaseAddress };
        other.DefaultRequestHeaders.Host = "evil.example:8642";
        using var refused = await other.GetAsync("/v1/languages");
        Assert.Equal((HttpStatusCode)421, refused.StatusCode);
        Assert.Contains("unexpected_host", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/v1/health")).StatusCode);     // the health check says nothing private
        Assert.Equal(HttpStatusCode.OK, (await other.SendAsync(Browser("/"))).StatusCode);    // and neither does the page

        using var named = new HttpClient { BaseAddress = api.Client.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await named.GetAsync("/v1/languages")).StatusCode);   // addressed by its IP address, as normal

        await using var protectedApi = await Harness.StartAsync();
        using var withPassword = new HttpClient { BaseAddress = protectedApi.Client.BaseAddress };
        withPassword.DefaultRequestHeaders.Host = "evil.example:8642";
        withPassword.DefaultRequestHeaders.Authorization = protectedApi.Client.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.OK, (await withPassword.GetAsync("/v1/languages")).StatusCode);  // the password is what protects it
    }

    [Fact]
    public void ThePageNeverBuildsMarkupFromWhatTheServerSendsAndNeverReachesOutsideItsServer()
    {
        var files = Directory.GetFiles(WebFolder()).ToArray();
        Assert.Equal(["app.css", "app.js", "index.html", "live.js", "worklet.js"], files.Select(path => Path.GetFileName(path)!).Order().ToArray());
        foreach (var path in files)
        {
            var source = File.ReadAllText(path);
            foreach (var forbidden in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function", "setTimeout(\"", "javascript:" })
                Assert.DoesNotContain(forbidden, source);
            // The only address that appears is the namespace of the SVG elements; the page talks to the server it came from, by relative addresses.
            var addresses = Regex.Matches(source, "https?://[^\\s\"'`)<>]+").Select(match => match.Value).Where(value => value != "http://www.w3.org/2000/svg" && !value.StartsWith("http://www.w3.org/2000/svg")).ToArray();
            Assert.True(addresses.Length == 0, Path.GetFileName(path) + ": " + string.Join(", ", addresses));
        }
        Assert.DoesNotContain("<script>", File.ReadAllText(Path.Combine(WebFolder(), "index.html")));
    }

    [Fact]
    public void ThePageSendsThePasswordOnlyInAHeaderAndKeepsItOnlyForTheTab()
    {
        var script = File.ReadAllText(Path.Combine(WebFolder(), "app.js"));
        Assert.DoesNotContain("localStorage.setItem(\"mb-password\"", script);
        Assert.Contains("keep.set(\"sessionStorage\", \"mb-password\"", script);
        Assert.DoesNotContain("password=", script);        // never in an address
        Assert.DoesNotContain("?key=", script);
        Assert.Contains("\"Authorization\"", script);
    }
    [Fact]
    public void ThePageAsksTheServerToFetchALinkAndNeverFetchesAnythingItself()
    {
        var script = File.ReadAllText(Path.Combine(WebFolder(), "app.js"));
        Assert.Contains("api(\"/v1/links\", { method: \"POST\"", script);
        Assert.Contains("JSON.stringify({ url, language: ui.language.value })", script);
        Assert.Contains("info.linksEnabled", script);                      // the button follows what the server says it can do
        Assert.Contains("info.linkPages", script);
        Assert.Contains("t(job.error)", script);                           // a link that failed says why in the page's language
        Assert.DoesNotContain("fetch(url", script);                        // the address the person typed is only ever handed to the server
        Assert.DoesNotContain("window.open", script);
        Assert.DoesNotContain("location.href =", script);
    }

    [Fact]
    public async Task ThePagesTranslationsIncludeWhyALinkFailedAndWhatAServerDoesWhileItFetchesOne()
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        foreach (var code in new[] { "hu", "de", "es", "fr" })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(await anonymous.GetStringAsync("/ui/strings.json?lang=" + code))!;
            foreach (var message in TriAsr.Application.LinkMessages.All.Append(TriAsr.Application.TranscriptionProgressTracker.LinkStage))
            {
                Assert.True(table.ContainsKey(message), $"{code}: {message}");
                Assert.NotEqual(message, table[message]);
            }
            foreach (var text in new[] { "Link", "Web address", "Send link to server", "The link could not be sent: {0}" }) Assert.True(table.ContainsKey(text), $"{code}: {text}");
        }
    }
    [Fact]
    public void ThePageOpensAFinishedRecordingWhenItIsClickedAndDeletesOneOnlyAfterAskingAndNeverWhileItIsBeingWorkedOn()
    {
        var script = File.ReadAllText(Path.Combine(WebFolder(), "app.js"));
        Assert.Contains("onClick: () => openReview(job.id), onKeydown: (event) => { if (event.key === \"Enter\") openReview(job.id); }", script);   // a click or Enter on a finished one
        Assert.Contains("job.state === \"complete\"\n          ? { class: \"info open\"", script.Replace("\r\n", "\n"));
        Assert.Contains("(job.state === \"complete\" || job.state === \"failed\" || job.state === \"cancelled\") && h(\"button\"", script);   // the button only where deleting is possible
        Assert.Contains("window.confirm(t(\"Delete \\\"{0}\\\" from the server?", script);                                                        // after asking
        Assert.Contains("api(\"/v1/transcriptions/\" + job.id, { method: \"DELETE\" })", script);
        Assert.Contains("error.code === \"still_running\"", script);
        Assert.Contains("if (state.review && state.review.id === job.id) closeReview();", script);                                              // an open review of it is closed
        Assert.Contains("URL.revokeObjectURL(state.review.audio)", script);
        Assert.Contains("t(\"Click a finished project to open its transcript.\")", script);
    }

    [Fact]
    public async Task ThePagesTranslationsIncludeTheTextsOfTheHistory()
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        foreach (var code in new[] { "hu", "de", "es", "fr" })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(await anonymous.GetStringAsync("/ui/strings.json?lang=" + code))!;
            foreach (var text in new[]
            {
                "Click a finished project to open its transcript.", "Delete", "Delete project", "Delete this project and its transcript", "Could not delete the project",
                "A project that is still being worked on cannot be deleted. Cancel it first.",
                "Delete \"{0}\" from the server? Its transcript, the edits and the recording the server holds are removed. This cannot be undone."
            })
            {
                Assert.True(table.ContainsKey(text), $"{code}: {text}");
                Assert.NotEqual(text, table[text]);
            }
        }
    }

    [Fact]
    public void ThePageKeepsNotesOnTheServerAndSavesThemWithTheRevisionItOpened()
    {
        var script = File.ReadAllText(Path.Combine(WebFolder(), "app.js"));
        Assert.Contains("api(\"/v1/notes\")", script);                                              // the list
        Assert.Contains("api(\"/v1/notes/\" + id, { method: \"PUT\"", script);                      // a save ...
        Assert.Contains("JSON.stringify({ title, text: body, revision })", script);                 // ... names the revision that was opened
        Assert.Contains("error.code === \"note_changed\"", script);                                 // and a note that changed meanwhile is not overwritten
        Assert.Contains("{0} (my version)", script);                                                // what was written here is kept as a note of its own
        Assert.Contains("api(\"/v1/live?language=", script);                                        // a recorded phrase is read by the server
        Assert.Contains("info.liveEnabled", script);
        Assert.Contains("info.notesEnabled", script);
        Assert.Contains("api(\"/v1/notes/\" + open.id, { method: \"DELETE\" })", script);
        Assert.Contains("window.confirm(t(\"Delete \\\"{0}\\\"? The note and its text are removed.", script);   // a note is deleted only after asking
        Assert.Contains("keep.set(\"localStorage\", \"mb-notes-keys\"", script);                    // the chosen keys are a convenience of this browser, nothing the server needs
        Assert.DoesNotContain("localStorage.setItem(\"mb-notes-text", script);                      // the text of a note is never left in the browser
    }

    [Fact]
    public async Task ThePagesTranslationsIncludeTheTextsOfTheNotesAndOfTheKeys()
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        foreach (var code in new[] { "hu", "de", "es", "fr" })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(await anonymous.GetStringAsync("/ui/strings.json?lang=" + code))!;
            foreach (var message in TriAsr.Application.NoteMessages.All.Concat(TriAsr.Application.LiveMessages.All).Concat(
            [
                "Notes", "New note", "Untitled note", "Change keys", "Press the keys you want to use", "Press the keys together, then click Done.", "Done", "Turn off", "Not set",
                "Hold Alt (or Ctrl and Shift) while you press the key, or use a function key such as F9 on its own.", "The keys work while this page is open.", "Keys to start and stop recording",
                "Almost every program uses these keys (copying, pasting, closing and the like). Choose other keys.", "Listening…", "Reading what you said…", "Finishing…", "Saving…", "All changes are saved",
                "The note could not be saved: {0}", "This note was changed on another computer. What you wrote was kept as a new note called \"{0}\".", "This note was deleted on another computer. It was saved again."
            ]))
            {
                Assert.True(table.ContainsKey(message), $"{code}: {message}");
                if (!(code == "fr" && message == "Notes")) Assert.NotEqual(message, table[message]);          // the French word for notes is the English one
            }
        }
    }
}