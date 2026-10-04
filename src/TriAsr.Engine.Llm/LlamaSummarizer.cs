using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Engine.Llm;

/// <summary>
/// Writes the summary of a transcript with a local llama.cpp server started for it (ADR: summaries). The answer is held to a JSON shape by the
/// server's grammar, so it always parses. A transcript longer than the window is summarized part by part, and the parts are then joined.
/// </summary>
public sealed class LlamaSummarizer : IAsyncDisposable
{
    /// <summary>The model's window: a summary needs the whole conversation in view.</summary>
    public const int Window = 32768;
    /// <summary>What is kept free in the window for the instruction and the answer.</summary>
    private const int Reserve = 4096;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task<ProcessResult> _worker;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _log = new();
    private readonly string _model;

    /// <param name="gpu">Whether the model goes on the graphics card (llama.cpp uses the processor when there is none).</param>
    public LlamaSummarizer(IProcessRunner runner, string executable, string model, bool gpu, int threads)
    {
        _model = Path.GetFileName(model);
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        _worker = runner.RunAsync(new(executable, ["-m", model, "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--api-key", key, "-c", Window.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ngl", gpu ? "99" : "0", "--parallel", "1", "-fa", "auto"],
            Path.GetDirectoryName(executable)!, TimeSpan.FromHours(2), line => { _log.Enqueue(line); while (_log.Count > 60) _log.TryDequeue(out _); }), _lifetime.Token);
    }

    /// <summary>Waits until the model is loaded; throws when the server gives up (a graphics card without room for it, for one).</summary>
    public async Task StartAsync(CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (_worker.IsCompleted)
            {
                var exit = await _worker;
                throw new InvalidOperationException($"The summary model stopped while loading ({exit.ExitCode}). " + string.Join(" ", _log.TakeLast(3)));
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(1));
                using var response = await _http.GetAsync("health", timeout.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(250, token);
        }
        throw new TimeoutException("The summary model did not load in five minutes.");
    }

    /// <summary>The summary of a transcript in its own language. <paramref name="progress"/> goes from 0 to 1 over the parts of a long one.</summary>
    public async Task<MeetingSummary> SummarizeAsync(FinalTranscript transcript, IProgress<double>? progress, CancellationToken token)
    {
        var language = Summaries.LanguageName(transcript.Language);
        var paragraphs = Summaries.Paragraphs(transcript);
        if (paragraphs.Count == 0) throw new InvalidOperationException("The transcript has no text to summarize.");
        var whole = string.Join("\n", paragraphs);
        var tokens = await CountAsync(whole, token);
        progress?.Report(0.05);
        MeetingSummary result;
        if (tokens <= Window - Reserve) result = await AskAsync(Summaries.Instruction(language), "Transcript:\n" + whole, transcript.Language, token);
        else
        {
            // Long: each part on its own, then the parts joined. The length of a paragraph in tokens is estimated from the length of the whole.
            var perCharacter = (double)tokens / Math.Max(1, whole.Length);
            var parts = Summaries.Parts(paragraphs, (Window - Reserve) / 2, text => (int)Math.Ceiling(text.Length * perCharacter));
            var summaries = new List<MeetingSummary>();
            for (var i = 0; i < parts.Count; i++)
            {
                summaries.Add(await AskAsync(Summaries.Instruction(language), $"Transcript, part {i + 1} of {parts.Count}:\n" + parts[i], transcript.Language, token));
                progress?.Report(0.05 + 0.85 * (i + 1) / parts.Count);
            }
            var joined = string.Join("\n\n", summaries.Select((summary, i) => $"Part {i + 1}:\n" + Summaries.AsText(summary)));
            result = await AskAsync(Summaries.MergeInstruction(language), joined, transcript.Language, token);
        }
        progress?.Report(1);
        return result;
    }

    private async Task<int> CountAsync(string text, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync("tokenize", new { content = text }, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("tokens").GetArrayLength();
    }

    private async Task<MeetingSummary> AskAsync(string instruction, string content, string language, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync("v1/chat/completions", new
        {
            messages = new[] { new { role = "system", content = instruction }, new { role = "user", content } },
            temperature = 0.0, seed = 1, max_tokens = 2000,
            response_format = new { type = "json_schema", json_schema = new { name = "summary", schema = Summaries.Schema } }
        }, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The summary model refused the request ({(int)response.StatusCode}). " + await response.Content.ReadAsStringAsync(token));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var answer = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return Read(answer, language, _model);
    }

    /// <summary>Reads the model's JSON answer; what is missing is empty, and empty lines are dropped.</summary>
    public static MeetingSummary Read(string answer, string language, string model)
    {
        using var document = JsonDocument.Parse(answer);
        var root = document.RootElement;
        string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
        string[] Lines(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim()).Where(line => line.Length > 0).Distinct().ToArray() : [];
        var actions = root.TryGetProperty("actionItems", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => new SummaryAction(Text(item, "who"), Text(item, "what"), Text(item, "when")))
                .Where(item => item.What.Length > 0).ToArray() : [];
        return new MeetingSummary(Text(root, "summary"), Lines("keyPoints"), Lines("decisions"), actions, Lines("openQuestions"), language, model, DateTimeOffset.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await _worker; } catch (OperationCanceledException) { }
        finally { _http.Dispose(); _lifetime.Dispose(); }
    }
}
