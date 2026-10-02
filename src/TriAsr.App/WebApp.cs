using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>
/// The web page of a server: the pages of the desktop Client in a browser, from the same address as the API. The page itself holds nothing
/// private, so it is served to anyone who asks; what it shows comes from the API, which asks for the password. A few small files are embedded in the
/// program (<c>Web/</c>), with no library and no address outside this server, which is what lets the policy below forbid everything else.
/// </summary>
public static partial class WebApp
{
    /// <summary>The page may load its own script and style, show media it made itself (the recording it fetched), and talk to its own server. Nothing else.</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src blob:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly ConcurrentDictionary<string, byte[]> Embedded = new();
    private static readonly Lazy<string[]> Wanted = new(WantedTexts);

    /// <summary>The page, its files and its translations, or null when the request is for something else (the API, the text page).</summary>
    public static HttpResponse? Serve(HttpRequest request)
    {
        if (request.Method is not ("GET" or "HEAD")) return null;
        return request.Path switch
        {
            "/" when WantsPage(request) => File("index.html", "text/html; charset=utf-8"),
            "/app.js" => File("app.js", "text/javascript; charset=utf-8"),
            "/app.css" => File("app.css", "text/css; charset=utf-8"),
            "/live.js" => File("live.js", "text/javascript; charset=utf-8"),
            "/worklet.js" => File("worklet.js", "text/javascript; charset=utf-8"),
            "/ui/strings.json" => Strings(request),
            _ => null
        };
    }

    /// <summary>A browser asks for a page; a program (curl, a client library) asks for anything and gets the plain text overview as before.</summary>
    private static bool WantsPage(HttpRequest request) => request.Header("Accept")?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true;

    private static HttpResponse File(string name, string contentType) => Secure(HttpResponse.Bytes(200, Resource(name), contentType));

    // The HTTP server already adds "Cache-Control: no-store" and "X-Content-Type-Options: nosniff" to every response.
    private static HttpResponse Secure(HttpResponse response) => response
        .With("Content-Security-Policy", ContentSecurityPolicy)
        .With("X-Frame-Options", "DENY")
        .With("Referrer-Policy", "no-referrer");

    private static byte[] Resource(string name) => Embedded.GetOrAdd(name, key =>
    {
        using var stream = typeof(WebApp).Assembly.GetManifestResourceStream("Mockingbird.Web." + key) ?? throw new InvalidOperationException($"The web page file {key} is not part of the program.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    // ---- the words of the page -------------------------------------------------------------------------------------------------------

    private static HttpResponse Strings(HttpRequest request)
    {
        var code = request.Query.GetValueOrDefault("lang") ?? "en";
        if (!Loc.IsSupported(code)) return HttpResponse.Error(400, "bad_language", "lang must be one of: " + string.Join(", ", Loc.Languages.Select(language => language.Code)) + ".");
        var table = Loc.Load(code);
        var strings = Wanted.Value.Where(table.ContainsKey).ToDictionary(text => text, text => table[text], StringComparer.Ordinal);
        return Secure(HttpResponse.Json(200, strings));
    }

    /// <summary>
    /// The texts the page looks up: the ones its script asks for with <c>t("...")</c>, and the names that come from the server in English (the languages
    /// and the stages of a transcription), which the page translates in the same way.
    /// </summary>
    private static string[] WantedTexts()
    {
        var texts = new SortedSet<string>(StringComparer.Ordinal);
        var script = System.Text.Encoding.UTF8.GetString(Resource("app.js"));
        foreach (Match match in ScriptText().Matches(script)) texts.Add(Unescape(match.Groups[1].Value));
        foreach (var language in LanguageCatalog.All) texts.Add(language.Name);
        foreach (var stage in Enum.GetValues<JobState>()) texts.Add(TranscriptionProgressTracker.StageName(stage));
        texts.Add("Whisper and Canary transcription");
        texts.Add(TranscriptionProgressTracker.LinkStage);               // what a recording is doing while its link is fetched
        texts.UnionWith(LinkMessages.All);                               // why a link failed, in the words of the layers that fetch it
        texts.UnionWith(NoteMessages.All);                               // why a note could not be saved
        texts.UnionWith(LiveMessages.All);                               // why a phrase could not be read
        texts.UnionWith(["agreement", "uncertain", "manual"]);          // how a region came about (the two others are spelled out by the script)
        texts.Add(TriAsr.Fusion.TranscriptQuality.RepetitionWarning);    // a warning the review can carry
        return [.. texts];
    }

    [GeneratedRegex(@"(?<![\w.])t\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex ScriptText();

    /// <summary>The characters a script string spells with a backslash (the same rule as in the program's own texts).</summary>
    private static string Unescape(string literal) => Regex.Replace(literal, @"\\(u[0-9a-fA-F]{4}|.)", match => match.Groups[1].Value switch
    {
        "n" => "\n", "t" => "\t", "r" => "\r",
        var unicode when unicode.Length == 5 && unicode[0] == 'u' => ((char)Convert.ToInt32(unicode[1..], 16)).ToString(),
        var other => other
    });

    // ---- who the request says it is for ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a request is addressed to this server by a name it expects: an IP address, <c>localhost</c> or the name of this computer. A page on another
    /// site cannot make a browser send such a request to a server on the local network (DNS rebinding) unless it can also change the name in the address,
    /// and with a password the server does not need to care. A server without one therefore only answers to these names.
    /// </summary>
    public static bool IsExpectedHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader)) return true;
        var host = hostHeader.Trim();
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            host = end > 0 ? host[1..end] : host;
        }
        else if (host.IndexOf(':') is var colon and > 0 && colon == host.LastIndexOf(':')) host = host[..colon];
        if (IPAddress.TryParse(host, out _)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var machine = Environment.MachineName;
        return host.Equals(machine, StringComparison.OrdinalIgnoreCase) || host.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase);
    }
}
