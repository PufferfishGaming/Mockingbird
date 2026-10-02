using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using TriAsr.App;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The web page of a server (ADR-0015): served to a browser without a password, locked down by its policy, translated, and the API behind it unchanged.</summary>
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
        foreach (var (path, type) in new[] { ("/", "text/html"), ("/app.js", "text/javascript"), ("/app.css", "text/css"), ("/ui/strings.json?lang=hu", "application/json") })
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
        Assert.Equal(["app.css", "app.js", "index.html"], files.Select(path => Path.GetFileName(path)!).Order().ToArray());
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
}
