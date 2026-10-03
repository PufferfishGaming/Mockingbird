using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Channels;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Export;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>What the API needs from the program. Everything is passed in so that tests can run the whole API with fake engines.</summary>
/// <param name="IncomingFolder">Where uploads are kept. A job whose source lies here was sent through the API; only those are visible to the API.</param>
/// <param name="ExportFolder">Scratch space for building a transcript file in a requested format.</param>
/// <param name="GetPassword">The password, read for every request so that a new one takes effect at once. Empty means the server is open to anyone who can reach it.</param>
/// <param name="GetName">The name the server goes by on the network.</param>
/// <param name="Edition">Studio or Server (the edition that hosts).</param>
/// <param name="LoadReview">The transcript as reviewed and as the programs wrote it, with the raw engine texts, for the remote review page.</param>
/// <param name="SaveReview">Stores an edited transcript.</param>
/// <param name="AudioPath">The file of a job's audio (<c>playback</c> or <c>normalized</c>), or null.</param>
/// <param name="MissingModels">Names of the speech models a language still needs; empty when it can run.</param>
/// <param name="CanRun">False while the program is busy with something that must not overlap (model download, tuning, setup).</param>
/// <param name="BusyChanged">Told when the API starts and stops working on a job.</param>
/// <param name="Links">Fetches the sound of a web address for <c>POST /v1/links</c>. Null: this server does not fetch links.</param>
/// <param name="Live">Reads one phrase of live dictation for <c>POST /v1/live</c>. Null: this server does not read dictation.</param>
/// <param name="LiveReady">Whether <paramref name="Live"/> can read a phrase right now (a speech model is installed); null means always.</param>
/// <param name="Notes">Keeps the notes for <c>/v1/notes</c>. Null: this server does not keep notes.</param>
/// <param name="DeleteJob">Deletes a finished recording with everything stored for it, for <c>DELETE /v1/transcriptions/{id}</c>. Null: this server does not delete recordings.</param>
/// <param name="SpeakersReady">Whether this server can tell the speakers of a recording apart (the speaker program and its models are installed); null means it cannot.</param>
public sealed record ApiServiceDependencies(AudioJobQueue Queue, TranscriptionPipeline Pipeline, IJobRepository Repository,
    Func<Guid, CancellationToken, Task<FinalTranscript>> LoadTranscript, string IncomingFolder, string ExportFolder, string Version,
    Func<string> GetPassword, Func<string, string[]> MissingModels, Func<bool> CanRun, Action<bool> BusyChanged,
    Func<string> GetName, string Edition, Func<Guid, CancellationToken, Task<ReviewBundle>> LoadReview, Func<FinalTranscript, CancellationToken, Task> SaveReview,
    Func<Guid, string, string?> AudioPath, ILinkFetcher? Links = null, Func<TranscriptionJob, CancellationToken, Task>? DeleteJob = null,
    ILiveRecognizer? Live = null, Func<bool>? LiveReady = null, INoteStore? Notes = null, Func<bool>? SpeakersReady = null);

/// <summary>Everything the remote review page needs about a finished job.</summary>
public sealed record ReviewBundle(FinalTranscript Review, FinalTranscript Automatic, string RawWhisper, string RawCanary, string? RawCanaryNote);

/// <summary>
/// The program's HTTP API: upload a recording, follow its progress, fetch the transcript, and an OpenAI-compatible
/// <c>/v1/audio/transcriptions</c> so that tools written for that API work unchanged. Every request but the two that only say the server is there
/// needs the key. Jobs run one at a time through the same pipeline as the window's own, and the API sees only what was sent through it.
/// </summary>
public sealed class ApiService : IAsyncDisposable
{
    private const int MaxPendingJobs = 50;

    private sealed class ApiJob(TranscriptionJob job, string name)
    {
        public Guid Id { get; } = job.Id;
        public string Name { get; set; } = name;
        public TranscriptionJob Job { get; set; } = job;
        /// <summary>What else was asked for the recording (telling its speakers apart).</summary>
        public JobOptions Options { get; init; } = new();
        /// <summary>A link whose sound is still being fetched: the job exists only here (as queued) until the file has arrived and the real job is made with this id.</summary>
        public bool Fetching { get; set; }
        public double FetchPercent { get; set; }
        public TranscriptionProgress? Progress { get; set; }
        public CancellationTokenSource? Running { get; set; }
        public bool CancelRequested { get; set; }
    }

    private static readonly string[] Formats = ["json", "full-json", "txt", "md", "srt", "vtt", "csv", "docx"];

    private readonly ApiServiceDependencies _deps;
    private readonly AuthThrottle _throttle = new();
    private readonly ConcurrentDictionary<Guid, ApiJob> _jobs = new();
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly CancellationTokenSource _stop = new();
    private Task? _runner;

    /// <summary>Raised when the API itself changes a job (cancelling one that never started), so that the window's lists can follow.</summary>
    public event EventHandler<TranscriptionJob>? JobChangedByApi;

    /// <summary>Raised when a client deleted a recording through the API, so that the window's list drops it too.</summary>
    public event EventHandler<Guid>? JobDeletedByApi;

    /// <summary>The window deleted a project: the API stops listing it.</summary>
    public void Forget(Guid id) => _jobs.TryRemove(id, out _);

    public ApiService(ApiServiceDependencies dependencies)
    {
        _deps = dependencies;
        _deps.Queue.JobChanged += OnJobChanged;
        _deps.Pipeline.JobChanged += OnJobChanged;
        _deps.Pipeline.ProgressChanged += OnProgress;
    }

    /// <summary>Picks up uploads from an earlier run: those still waiting are run now, the others stay listed.</summary>
    public async Task StartAsync(CancellationToken token = default)
    {
        Directory.CreateDirectory(_deps.IncomingFolder);
        foreach (var job in await _deps.Repository.ListAsync(token))
        {
            if (!IsApiSource(job.SourcePath)) continue;
            var entry = new ApiJob(job, Path.GetFileName(job.SourcePath));
            if (!_jobs.TryAdd(job.Id, entry)) continue;
            if (job.State == JobState.Queued && File.Exists(job.SourcePath)) _queue.Writer.TryWrite(job.Id);
        }
        _runner = Task.Run(RunAsync);
    }

    private bool IsApiSource(string path) =>
        path.StartsWith(Path.GetFullPath(_deps.IncomingFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void OnJobChanged(object? sender, TranscriptionJob job) { if (_jobs.TryGetValue(job.Id, out var entry)) entry.Job = job; }
    private void OnProgress(object? sender, TranscriptionProgress progress) { if (_jobs.TryGetValue(progress.JobId, out var entry)) entry.Progress = progress; }

    // ---- the one-at-a-time runner -----------------------------------------------------------------------------------------------------

    private async Task RunAsync()
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                if (!_jobs.TryGetValue(id, out var entry) || entry.CancelRequested || entry.Job.State != JobState.Queued) continue;
                while (!_deps.CanRun()) await Task.Delay(500, _stop.Token);
                _deps.BusyChanged(true);
                using var running = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                entry.Running = running;
                try { entry.Job = await _deps.Pipeline.RunAsync(entry.Job, running.Token); }
                catch (Exception error) when (error is not OperationCanceledException) { entry.Job = entry.Job with { State = JobState.Failed, Error = error.Message }; }
                finally
                {
                    entry.Running = null;
                    _deps.BusyChanged(_queue.Reader.Count > 0);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { _deps.BusyChanged(false); }
    }

    public async ValueTask DisposeAsync()
    {
        _deps.Queue.JobChanged -= OnJobChanged;
        _deps.Pipeline.JobChanged -= OnJobChanged;
        _deps.Pipeline.ProgressChanged -= OnProgress;
        _queue.Writer.TryComplete();
        _stop.Cancel();
        if (_runner is not null) { try { await _runner.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { } }
        _stop.Dispose();
    }

    // ---- requests ---------------------------------------------------------------------------------------------------------------------

    public async Task<HttpResponse> HandleAsync(HttpRequest request, CancellationToken token)
    {
        if (WebApp.Serve(request) is { } page) return page; // the web page and its files: nothing private in them, and the API behind them asks for the password
        var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return request.Method == "GET" ? RootPage() : MethodNotAllowed("GET");
        if (segments is ["v1", "health"])
            return request.Method == "GET"
                ? HttpResponse.Json(200, new RemoteHealth("ok", _deps.GetName(), _deps.Edition, _deps.Version, _deps.GetPassword().Length > 0, request.IsSecure))
                : MethodNotAllowed("GET");
        if (segments[0] != "v1") return NotFound();

        if (CheckHost(request) is { } wrongHost) return wrongHost;
        if (await CheckPasswordAsync(request) is { } refusal) return refusal;

        switch (segments)
        {
            case ["v1", "server"]:
                return request.Method == "GET" ? Info(request) : MethodNotAllowed("GET");
            case ["v1", "models"]:
                return request.Method == "GET" ? HttpResponse.Json(200, new { @object = "list", data = new[] { new { id = "mockingbird-studio", @object = "model", owned_by = "local" } } }) : MethodNotAllowed("GET");
            case ["v1", "languages"]:
                return request.Method == "GET" ? Languages() : MethodNotAllowed("GET");
            case ["v1", "transcriptions"]:
                return request.Method switch { "POST" => await UploadAsync(request, token), "GET" => List(), _ => MethodNotAllowed("GET, POST") };
            case ["v1", "live"]:
                return request.Method == "POST" ? await LiveAsync(request, token) : MethodNotAllowed("POST");
            case ["v1", "links"]:
                return request.Method == "POST" ? await LinkAsync(request, token) : MethodNotAllowed("POST");
            case ["v1", "notes"]:
                return request.Method switch { "GET" => await NotesAsync(token), "POST" => await CreateNoteAsync(request, token), _ => MethodNotAllowed("GET, POST") };
            case ["v1", "notes", var id]:
                return request.Method switch { "GET" => await NoteAsync(id, token), "PUT" => await SaveNoteAsync(id, request, token), "DELETE" => await DeleteNoteAsync(id, token), _ => MethodNotAllowed("GET, PUT, DELETE") };
            case ["v1", "transcriptions", var id]:
                return request.Method switch { "GET" => await StatusAsync(id, request, token), "DELETE" => await DeleteRecordingAsync(id, token), _ => MethodNotAllowed("GET, DELETE") };
            case ["v1", "transcriptions", var id, "transcript"]:
                return request.Method == "GET" ? await TranscriptAsync(id, request, token) : MethodNotAllowed("GET");
            case ["v1", "transcriptions", var id, "cancel"]:
                return request.Method == "POST" ? await CancelAsync(id) : MethodNotAllowed("POST");
            case ["v1", "transcriptions", var id, "review"]:
                return request.Method switch { "GET" => await ReviewAsync(id, token), "PUT" => await SaveEditsAsync(id, request, token), _ => MethodNotAllowed("GET, PUT") };
            case ["v1", "transcriptions", var id, "audio"]:
                return request.Method is "GET" or "HEAD" ? Audio(id, request) : MethodNotAllowed("GET");
            case ["v1", "audio", "transcriptions"]:
                return request.Method == "POST" ? await OpenAiTranscriptionAsync(request, token) : MethodNotAllowed("POST");
            default:
                return NotFound();
        }
    }

    private static HttpResponse NotFound() => HttpResponse.Error(404, "not_found", "There is nothing at this address.");
    private static HttpResponse MethodNotAllowed(string allow) => HttpResponse.Error(405, "method_not_allowed", "This address does not accept that method.").With("Allow", allow);

    private HttpResponse RootPage() => HttpResponse.Text(200,
        $"Mockingbird {_deps.Edition} {_deps.Version}: local transcription API.\n\nGET  /v1/health                        server check (no password needed)\nGET  /v1/languages                     the languages\nPOST /v1/transcriptions?language=auto  upload a recording (the request body is the file)\nPOST /v1/live?language=auto             read one phrase of live dictation (the body is a WAV file)\nGET  /v1/notes, POST /v1/notes             the notes kept on this server (JSON: title, text)\nGET  /v1/notes/{{id}}, PUT, DELETE        read, save (with the revision you read) and delete a note\nPOST /v1/links                         fetch the sound of a web address and transcribe it (JSON: url, language; needs a password)\nGET  /v1/transcriptions/{{id}}           state and progress (?wait=30 waits for the end)\nGET  /v1/transcriptions/{{id}}/transcript?format=json|txt|md|srt|vtt|csv|docx\nPOST /v1/transcriptions/{{id}}/cancel\nDELETE /v1/transcriptions/{{id}}         delete a finished recording with its transcript\nPOST /v1/audio/transcriptions          OpenAI-compatible (multipart form: file, language, response_format)\n\nOpen this address in a browser for the web page. Send the password (if the server has one) as \"Authorization: Bearer <password>\".\n");

    // ---- who the request is for -------------------------------------------------------------------------------------------------------

    /// <summary>A server without a password only answers requests addressed to its IP address, localhost or its computer's name (see <see cref="WebApp.IsExpectedHost"/>).</summary>
    private HttpResponse? CheckHost(HttpRequest request) =>
        _deps.GetPassword().Length > 0 || WebApp.IsExpectedHost(request.Header("Host")) ? null
            : HttpResponse.Error(421, "unexpected_host", "This server has no password, so it only answers requests addressed to its IP address, localhost or the name of its computer. Use one of those, or set a password.");

    // ---- the password -----------------------------------------------------------------------------------------------------------------

    private async Task<HttpResponse?> CheckPasswordAsync(HttpRequest request)
    {
        var expected = _deps.GetPassword();
        if (expected.Length == 0) return null; // the owner chose a server that anyone who can reach it may use
        var address = request.Remote?.Address is { } remote ? (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString() : "unknown";
        if (_throttle.IsBlocked(address))
            return HttpResponse.Error(429, "too_many_attempts", "Too many wrong passwords from this address. Try again in a minute.").With("Retry-After", "60");
        var given = request.Header("X-Api-Key");
        if (request.Header("Authorization") is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) given = header[7..].Trim();
        if (AuthThrottle.SecretsEqual(given, expected))
        {
            _throttle.RecordSuccess(address);
            return null;
        }
        _throttle.RecordFailure(address);
        await Task.Delay(250); // guessing is slow even before the lockout
        return HttpResponse.Error(401, "unauthorized", "The password is missing or wrong. Send it as \"Authorization: Bearer <password>\".").With("WWW-Authenticate", "Bearer");
    }

    // ---- uploads ----------------------------------------------------------------------------------------------------------------------

    private static string SafeName(string? name, string fallback)
    {
        var clean = Path.GetFileName((name ?? "").Replace('\\', '/'));
        clean = string.Concat(clean.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c)).Trim(' ', '.');
        if (clean.Length == 0) clean = fallback;
        var extension = Path.GetExtension(clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        if (extension.Length > 12) extension = "";
        if (stem.Length > 80) stem = stem[..80];
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 3 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[2])) stem = "_" + stem;
        return stem + extension;
    }

    /// <summary>
    /// The language of a recording or a phrase: <c>auto</c> (or nothing), a code, or two joined with <c>+</c> (<c>en+hu</c>) for speech that switches between them.
    /// A plus in a web address arrives as a space, which is read the same.
    /// </summary>
    private static string? LanguageProblem(string? language, out string code)
    {
        if (LanguageCatalog.TryParseChoice(language, out var codes)) { code = LanguageCatalog.JoinChoice(codes); return null; }
        code = "auto";
        return $"\"{language?.Trim()}\" is not a supported language, or names more than {LanguageCatalog.MaxChoice}. GET /v1/languages lists them; two are joined with + (en+hu).";
    }

    private async Task<(string Path, string Name, long Bytes)> SaveUploadAsync(Stream content, string? name, long? declaredLength, CancellationToken token)
    {
        var safe = SafeName(name, "recording");
        var folder = Path.Combine(_deps.IncomingFolder, Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, safe);
        Directory.CreateDirectory(folder);
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
            if (free < (declaredLength ?? 0) + 512L * 1024 * 1024) throw new HttpProtocolException(507, "insufficient_storage", "There is not enough free disk space for this upload.");
            long total = 0;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, token)) > 0) { await file.WriteAsync(buffer.AsMemory(0, read), token); total += read; }
            }
            if (total == 0) throw new HttpProtocolException(400, "empty_upload", "The upload is empty. Send the recording as the request body.");
            return (path, safe, total);
        }
        catch (IOException error) when (error is not EndOfStreamException && IsDiskFull(error))
        {
            DeleteFolder(folder);
            throw new HttpProtocolException(507, "insufficient_storage", "The disk filled up during the upload.");
        }
        catch { DeleteFolder(folder); throw; }
    }

    private static bool IsDiskFull(IOException error) => (error.HResult & 0xFFFF) is 112 or 39;

    private static void DeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Why a new recording cannot be taken now (speech models missing, too many waiting), or null.</summary>
    private HttpResponse? CannotStart(string language, JobOptions? options = null)
    {
        if (options?.TellsSpeakersApart == true && _deps.SpeakersReady?.Invoke() != true)
            return HttpResponse.Error(409, "speakers_unavailable", "This server cannot tell speakers apart: its speaker program is not installed.");
        var missing = _deps.MissingModels(language);
        if (missing.Length > 0)
            return HttpResponse.Json(409, new { error = new { code = "models_missing", message = "The speech models for this language are not downloaded yet. Download them in the Models page of the app.", type = "invalid_request_error" }, missing });
        if (_jobs.Values.Count(job => job.Job.State is JobState.Queued) >= MaxPendingJobs)
            return HttpResponse.Error(429, "queue_full", "Too many recordings are waiting. Try again when some are done.").With("Retry-After", "60");
        return null;
    }

    private async Task<(ApiJob? Job, HttpResponse? Refusal)> StartJobAsync(string path, string name, string language, CancellationToken token, JobOptions? options = null)
    {
        options ??= new JobOptions();
        if (CannotStart(language, options) is { } refusal)
        {
            DeleteFolder(Path.GetDirectoryName(path)!);
            return (null, refusal);
        }
        var job = await _deps.Queue.EnqueueAsync(path, language, token, options: options);
        var entry = new ApiJob(job, name) { Options = options };
        _jobs[job.Id] = entry;
        _queue.Writer.TryWrite(job.Id);
        return (entry, null);
    }

    /// <summary>The speakers asked for: <c>off</c> (or nothing), <c>auto</c>, or how many there are (2 to 8).</summary>
    private static string? SpeakersProblem(string? speakers, out JobOptions options)
    {
        options = new JobOptions(JobOptions.NormalizeSpeakers(speakers) ?? "off");
        return JobOptions.NormalizeSpeakers(speakers) is null ? $"\"{speakers?.Trim()}\" is not a choice of speakers: give auto, off, or how many there are (2 to {JobOptions.MostSpeakers})." : null;
    }

    private async Task<HttpResponse> UploadAsync(HttpRequest request, CancellationToken token)
    {
        if (LanguageProblem(request.Query.GetValueOrDefault("language"), out var language) is { } problem) return HttpResponse.Error(400, "unsupported_language", problem);
        if (SpeakersProblem(request.Query.GetValueOrDefault("speakers"), out var options) is { } wrong) return HttpResponse.Error(400, "invalid_speakers", wrong);
        if (request.Header("Content-Type") is { } type && type.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return HttpResponse.Error(415, "use_raw_body", "Send the recording itself as the request body, or use POST /v1/audio/transcriptions for a multipart form.");
        var upload = await SaveUploadAsync(request.Body, request.Query.GetValueOrDefault("name"), request.ContentLength, token);
        var (job, refusal) = await StartJobAsync(upload.Path, upload.Name, language, token, options);
        if (refusal is not null) return refusal;
        return HttpResponse.Json(202, Describe(job!)).With("Location", $"/v1/transcriptions/{job!.Id}");
    }

    // ---- live dictation ------------------------------------------------------------------------------------------------------------

    private const int MaxLiveBody = 2 * 1024 * 1024;       // a minute of sound is 1.9 MB: far more than a phrase

    /// <summary>
    /// <c>POST /v1/live?language=xx</c>: the body is one phrase as a WAV file; the answer is <c>{"text": "...", "language": "xx"}</c>. This is what a Client types with while its person dictates:
    /// only the speech program runs (no second engine, no comparison), so that the answer comes back within a second or two. <c>language=en+hu</c> reads the phrase in
    /// whichever of the two it was spoken in; <c>recent</c> (the language the previous phrase came back in) decides a close call.
    /// </summary>
    private async Task<HttpResponse> LiveAsync(HttpRequest request, CancellationToken token)
    {
        if (_deps.Live is not { } live) return HttpResponse.Error(501, "live_unavailable", "This server does not read live dictation.");
        if (LanguageProblem(request.Query.GetValueOrDefault("language"), out var language) is { } problem) return HttpResponse.Error(400, "unsupported_language", problem);
        var recent = request.Query.GetValueOrDefault("recent")?.Trim().ToLowerInvariant() is { Length: > 0 } said && LanguageCatalog.Supports(said) ? said : null;
        if (request.ContentLength > MaxLiveBody) return HttpResponse.Error(413, "payload_too_large", LiveMessages.TooLong);
        using var buffer = new MemoryStream();
        var piece = new byte[64 * 1024]; int read;
        while ((read = await request.Body.ReadAsync(piece, token)) > 0)
        {
            if (buffer.Length + read > MaxLiveBody) return HttpResponse.Error(413, "payload_too_large", LiveMessages.TooLong);
            buffer.Write(piece, 0, read);
        }
        if (buffer.Length < 44) return HttpResponse.Error(400, "empty_upload", "Send one phrase as a WAV file in the request body.");
        try
        {
            // The words come back without the program's markers. A client that says how much of the phrase was speech (?speech=milliseconds) also gets nothing for the few words
            // Whisper invents for a cough or a click, as the programs' own dictation does.
            var phrase = await live.RecognizeAsync(buffer.ToArray(), language, recent, token);
            var text = PhraseText.Clean(phrase.Text);
            if (int.TryParse(request.Query.GetValueOrDefault("speech"), out var speech) && speech >= 0 && PhraseText.IsPhantom(text, TimeSpan.FromMilliseconds(speech))) text = "";
            return HttpResponse.Json(200, new { text, language = text.Length > 0 ? phrase.Language : "" });
        }
        catch (LiveException error) when (error.Message == LiveMessages.NoModel) { return HttpResponse.Error(409, "models_missing", error.Message); }
        catch (LiveException error) when (error.Message == LiveMessages.TooLong) { return HttpResponse.Error(413, "payload_too_large", error.Message); }
        catch (LiveException error) { return HttpResponse.Error(502, "live_failed", error.Message); }
    }

    // ---- notes ------------------------------------------------------------------------------------------------------------------------

    private const int MaxNoteBody = 8 * 1024 * 1024;       // a note of the longest allowed size, with room for the JSON around it

    private static RemoteNote Describe(UserNote note) => new(note.Id, note.Title, note.Text, note.CreatedUtc, note.UpdatedUtc, note.Revision);

    private async Task<HttpResponse> NotesAsync(CancellationToken token)
    {
        if (_deps.Notes is not { } notes) return NotesUnavailable();
        var list = await notes.ListAsync(token);
        return HttpResponse.Json(200, new RemoteNotes(list.Select(note => new RemoteNoteSummary(note.Id, note.Title, NoteText.Preview(note.Text), note.CreatedUtc, note.UpdatedUtc, note.Revision, note.Text.Length)).ToArray()));
    }

    private async Task<HttpResponse> NoteAsync(string id, CancellationToken token)
    {
        if (_deps.Notes is not { } notes) return NotesUnavailable();
        if (!Guid.TryParse(id, out var guid) || await notes.GetAsync(guid, token) is not { } note) return HttpResponse.Error(404, "not_found", NoteMessages.NotFound);
        return HttpResponse.Json(200, Describe(note));
    }

    private async Task<HttpResponse> CreateNoteAsync(HttpRequest request, CancellationToken token)
    {
        if (_deps.Notes is not { } notes) return NotesUnavailable();
        var (edit, refusal) = await ReadNoteEditAsync(request, token);
        if (refusal is not null) return refusal;
        try
        {
            var note = await notes.CreateAsync(edit!.Title ?? "", edit.Text ?? "", token);
            return HttpResponse.Json(201, Describe(note)).With("Location", $"/v1/notes/{note.Id}");
        }
        catch (NoteException error) { return NoteProblem(error); }
    }

    private async Task<HttpResponse> SaveNoteAsync(string id, HttpRequest request, CancellationToken token)
    {
        if (_deps.Notes is not { } notes) return NotesUnavailable();
        if (!Guid.TryParse(id, out var guid)) return HttpResponse.Error(404, "not_found", NoteMessages.NotFound);
        var (edit, refusal) = await ReadNoteEditAsync(request, token);
        if (refusal is not null) return refusal;
        try
        {
            // A save that leaves out a part keeps what is there; one that leaves out the revision saves over whatever is there.
            var current = await notes.GetAsync(guid, token);
            if (current is null) return HttpResponse.Error(404, "not_found", NoteMessages.NotFound);
            return HttpResponse.Json(200, Describe(await notes.SaveAsync(guid, edit!.Title ?? current.Title, edit.Text ?? current.Text, edit.Revision, token)));
        }
        catch (NoteException error) { return NoteProblem(error); }
    }

    private async Task<HttpResponse> DeleteNoteAsync(string id, CancellationToken token)
    {
        if (_deps.Notes is not { } notes) return NotesUnavailable();
        if (!Guid.TryParse(id, out var guid) || !await notes.DeleteAsync(guid, token)) return HttpResponse.Error(404, "not_found", NoteMessages.NotFound);
        return HttpResponse.Json(200, new { deleted = true });
    }

    private static HttpResponse NotesUnavailable() => HttpResponse.Error(501, "notes_unavailable", "This server does not keep notes.");

    private static HttpResponse NoteProblem(NoteException error) => error.Message switch
    {
        NoteMessages.NotFound => HttpResponse.Error(404, "not_found", error.Message),
        NoteMessages.Changed => HttpResponse.Error(409, "note_changed", error.Message),
        NoteMessages.TooLong => HttpResponse.Error(413, "payload_too_large", error.Message),
        _ => HttpResponse.Error(400, "bad_note", error.Message)
    };

    private static async Task<(RemoteNoteEdit?, HttpResponse?)> ReadNoteEditAsync(HttpRequest request, CancellationToken token)
    {
        if (request.ContentLength > MaxNoteBody) return (null, HttpResponse.Error(413, "payload_too_large", NoteMessages.TooLong));
        using var buffer = new MemoryStream();
        var piece = new byte[64 * 1024]; int read;
        while ((read = await request.Body.ReadAsync(piece, token)) > 0)
        {
            if (buffer.Length + read > MaxNoteBody) return (null, HttpResponse.Error(413, "payload_too_large", NoteMessages.TooLong));
            buffer.Write(piece, 0, read);
        }
        try
        {
            var edit = System.Text.Json.JsonSerializer.Deserialize<RemoteNoteEdit>(buffer.ToArray(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return edit is null ? (null, HttpResponse.Error(400, "bad_json", "Send JSON like {\"title\": \"…\", \"text\": \"…\"}.")) : (edit, null);
        }
        catch (System.Text.Json.JsonException) { return (null, HttpResponse.Error(400, "bad_json", "Send JSON like {\"title\": \"…\", \"text\": \"…\"}.")); }
    }

    // ---- links ------------------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// <c>POST /v1/links</c> {"url", "language"}: this server downloads the sound of a web address and transcribes it. Only a server with a password
    /// does this, because it makes the server fetch what a client names. The address is checked at once (public hosts only); the download runs in the background
    /// and the job is listed as running while it does.
    /// </summary>
    private async Task<HttpResponse> LinkAsync(HttpRequest request, CancellationToken token)
    {
        if (_deps.Links is null) return HttpResponse.Error(501, "links_unavailable", "This server does not fetch links.");
        if (_deps.GetPassword().Length == 0)
            return HttpResponse.Error(403, "links_need_password", "This server has no password, so it does not fetch links for other computers. Set a password on the server to allow it.");
        if (request.ContentLength > MaxLinkBody) return HttpResponse.Error(413, "payload_too_large", "The request is too large.");
        using var buffer = new MemoryStream();
        var piece = new byte[4096]; int read;
        while ((read = await request.Body.ReadAsync(piece, token)) > 0)
        {
            if (buffer.Length + read > MaxLinkBody) return HttpResponse.Error(413, "payload_too_large", "The request is too large.");
            buffer.Write(piece, 0, read);
        }
        RemoteLinkRequest? body;
        try { body = System.Text.Json.JsonSerializer.Deserialize<RemoteLinkRequest>(buffer.ToArray(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); }
        catch (System.Text.Json.JsonException) { return HttpResponse.Error(400, "bad_json", "Send JSON like {\"url\": \"https://…\", \"language\": \"auto\"}."); }
        if (body is null) return HttpResponse.Error(400, "bad_json", "Send JSON like {\"url\": \"https://…\", \"language\": \"auto\"}.");
        if (LanguageProblem(body.Language, out var language) is { } problem) return HttpResponse.Error(400, "unsupported_language", problem);
        if (SpeakersProblem(body.Speakers, out var options) is { } wrong) return HttpResponse.Error(400, "invalid_speakers", wrong);
        Uri link;
        try
        {
            link = LinkPolicy.Parse(body.Url);
            await _deps.Links.CheckAsync(link, allowPrivateNetwork: false, token);
        }
        catch (LinkException error) { return HttpResponse.Error(400, "invalid_link", error.Message); }
        if (CannotStart(language, options) is { } refusal) return refusal;

        var id = Guid.NewGuid();
        var folder = Path.Combine(_deps.IncomingFolder, id.ToString("N"));
        var entry = new ApiJob(new TranscriptionJob(id, Path.Combine(folder, "link"), language, JobState.Queued, DateTimeOffset.UtcNow), link.IdnHost) { Fetching = true, Options = options };
        var running = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        entry.Running = running;
        _jobs[id] = entry;
        _ = Task.Run(() => FetchLinkAsync(entry, link, folder, running));
        return HttpResponse.Json(202, Describe(entry)).With("Location", $"/v1/transcriptions/{id}");
    }

    private const int MaxLinkBody = 16 * 1024;

    /// <summary>Downloads the sound of a link, then makes the real job (with the id already given out) and queues it. A failure ends the entry with the reason.</summary>
    private async Task FetchLinkAsync(ApiJob entry, Uri link, string folder, CancellationTokenSource running)
    {
        try
        {
            var fetched = await _deps.Links!.FetchAsync(link, folder, new LinkFetchOptions(AllowPrivateNetwork: false),
                new Progress<double>(value => entry.FetchPercent = value), running.Token);
            running.Token.ThrowIfCancellationRequested();
            var job = await _deps.Queue.EnqueueAsync(fetched.Path, entry.Job.Language, running.Token, entry.Id, entry.Options);
            entry.Name = Path.GetFileName(fetched.Path);
            entry.Job = job;
            entry.Fetching = false;
            entry.Running = null;
            _queue.Writer.TryWrite(job.Id);
        }
        catch (OperationCanceledException) { EndFetch(entry, folder, JobState.Cancelled, null); }
        catch (LinkException error) { EndFetch(entry, folder, JobState.Failed, error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            EndFetch(entry, folder, JobState.Failed, "The link could not be downloaded.");
        }
        finally
        {
            entry.Running = null;
            running.Dispose();
        }
    }

    private void EndFetch(ApiJob entry, string folder, JobState state, string? error)
    {
        entry.Job = entry.Job with { State = state, Error = error };
        entry.Fetching = false;
        DeleteFolder(folder);
    }

    private async Task<HttpResponse> OpenAiTranscriptionAsync(HttpRequest request, CancellationToken token)
    {
        var boundary = MultipartReader.BoundaryOf(request.Header("Content-Type"));
        if (boundary is null) return HttpResponse.Error(400, "invalid_request", "Send a multipart/form-data form with a \"file\" field.");
        string? path = null, name = null, language = null, format = "json";
        try
        {
            var reader = new MultipartReader(request.Body, boundary);
            for (var count = 0; await reader.NextPartAsync(token) is { } part; count++)
            {
                if (count >= 20) { if (path is not null) DeleteFolder(Path.GetDirectoryName(path)!); return HttpResponse.Error(400, "invalid_request", "The form has too many fields."); }
                switch (part.Name)
                {
                    case "file":
                        if (path is not null) { DeleteFolder(Path.GetDirectoryName(path)!); return HttpResponse.Error(400, "invalid_request", "Send only one file."); }
                        (path, name, _) = await SaveUploadAsync(part.Content, part.FileName, request.ContentLength, token);
                        break;
                    case "language": language = await part.ReadTextAsync(64, token); break;
                    case "response_format": format = (await part.ReadTextAsync(64, token)).Trim().ToLowerInvariant(); break;
                }
            }
        }
        catch (InvalidDataException error)
        {
            if (path is not null) DeleteFolder(Path.GetDirectoryName(path)!);
            return HttpResponse.Error(400, "invalid_request", error.Message);
        }
        catch { if (path is not null) DeleteFolder(Path.GetDirectoryName(path)!); throw; }
        if (path is null) return HttpResponse.Error(400, "invalid_request", "The form has no \"file\" field.");
        if (format is not ("json" or "text" or "srt" or "vtt" or "verbose_json"))
        {
            DeleteFolder(Path.GetDirectoryName(path)!);
            return HttpResponse.Error(400, "invalid_request", "response_format must be json, text, srt, vtt or verbose_json.");
        }
        if (LanguageProblem(language, out var code) is { } problem) { DeleteFolder(Path.GetDirectoryName(path)!); return HttpResponse.Error(400, "unsupported_language", problem); }

        var (job, refusal) = await StartJobAsync(path, name!, code, token);
        if (refusal is not null) return refusal;
        while (job!.Job.State is not (JobState.Complete or JobState.Failed or JobState.Cancelled)) await Task.Delay(200, token);
        if (job.Job.State != JobState.Complete)
            return HttpResponse.Error(job.Job.State == JobState.Cancelled ? 409 : 500, job.Job.State == JobState.Cancelled ? "cancelled" : "transcription_failed", Plain(job.Job.Error ?? "The transcription did not finish."));

        var transcript = await _deps.LoadTranscript(job.Id, token);
        switch (format)
        {
            case "json": return HttpResponse.Json(200, new { text = JoinedText(transcript) });
            case "text": return HttpResponse.Text(200, JoinedText(transcript) + "\n");
            case "verbose_json":
                return HttpResponse.Json(200, new
                {
                    task = "transcribe", language = string.Join("+", transcript.Language.Split('+').Select(spoken => LanguageCatalog.All.FirstOrDefault(item => item.Code == spoken)?.Name.ToLowerInvariant() ?? spoken)),
                    duration = transcript.Regions.Count == 0 ? 0 : transcript.Regions.Max(region => region.EndMs) / 1000.0, text = JoinedText(transcript),
                    segments = transcript.Regions.Select((region, index) => new { id = index, start = region.StartMs / 1000.0, end = region.EndMs / 1000.0, text = region.FinalText })
                });
            default: return await RenderFileAsync(transcript, format, "strict", token);
        }
    }

    // ---- looking at jobs --------------------------------------------------------------------------------------------------------------

    private static string StateName(JobState state) => state switch
    {
        JobState.Queued => "queued", JobState.Complete => "complete", JobState.Failed => "failed", JobState.Cancelled => "cancelled", _ => "running"
    };

    /// <summary>An error message without the location of the upload on this computer.</summary>
    private string Plain(string message) => message.Replace(Path.GetFullPath(_deps.IncomingFolder), "<uploads>", StringComparison.OrdinalIgnoreCase);

    /// <summary>A link that is still being fetched counts as running: the server is working on it.</summary>
    private static string StateName(ApiJob entry) => entry.Fetching ? "running" : StateName(entry.Job.State);

    private object Describe(ApiJob entry)
    {
        var job = entry.Job; var state = StateName(entry);
        var percent = state switch
        {
            "complete" => 100,
            "running" when entry.Fetching => (int)Math.Round(entry.FetchPercent),
            "running" when entry.Progress is { } progress => (int)Math.Round(progress.Percent),
            _ => 0
        };
        return new
        {
            id = entry.Id, state, stage = entry.Fetching ? TranscriptionProgressTracker.LinkStage : state == "running" ? TranscriptionProgressTracker.StageName(job.State) : null, percent,
            language = job.Language, name = entry.Name, createdUtc = job.CreatedUtc, error = job.Error is null ? null : Plain(job.Error),
            links = new { self = $"/v1/transcriptions/{entry.Id}", transcript = $"/v1/transcriptions/{entry.Id}/transcript", cancel = $"/v1/transcriptions/{entry.Id}/cancel" }
        };
    }

    private HttpResponse List() => HttpResponse.Json(200, new { data = _jobs.Values.OrderByDescending(job => job.Job.CreatedUtc).Select(Describe).ToArray() });

    private HttpResponse Languages() => HttpResponse.Json(200, new
    {
        data = LanguageCatalog.All.Select(language => new { code = language.Code, name = language.Name, secondEngine = language.DualEngine }).Prepend(new { code = "auto", name = "Detect the language", secondEngine = false }).ToArray(),
        note = "secondEngine: Canary also transcribes this language and the two results are compared."
    });

    private HttpResponse Info(HttpRequest request)
    {
        var missing = _deps.MissingModels("auto");
        var password = _deps.GetPassword().Length > 0;
        return HttpResponse.Json(200, new RemoteServerInfo(_deps.GetName(), _deps.Edition, _deps.Version, request.IsSecure, password,
            missing.Length == 0, missing, _jobs.Values.Any(job => StateName(job) == "running"), _jobs.Values.Count(job => job.Job.State == JobState.Queued && !job.Fetching),
            LinksEnabled: password && _deps.Links is not null, LinkPages: _deps.Links?.PagesReady == true,
            LiveEnabled: _deps.Live is not null && (_deps.LiveReady?.Invoke() ?? true), NotesEnabled: _deps.Notes is not null, LanguagePairs: true, Speakers: _deps.SpeakersReady?.Invoke() == true));
    }

    private async Task<HttpResponse> ReviewAsync(string id, CancellationToken token)
    {
        if (!TryFind(id, out var entry)) return HttpResponse.Error(404, "not_found", "There is no such transcription.");
        if (entry.Job.State != JobState.Complete) return HttpResponse.Error(409, "not_ready", $"The transcription is {StateName(entry.Job.State)}; it can be reviewed once it is complete.");
        var bundle = await _deps.LoadReview(entry.Id, token);
        return HttpResponse.Json(200, new RemoteReview(entry.Id, bundle.Review.Language, bundle.Review.Regions, bundle.Automatic.Regions.Select(region => region.FinalText).ToArray(),
            bundle.RawWhisper, bundle.RawCanary, bundle.RawCanaryNote));
    }

    private async Task<HttpResponse> SaveEditsAsync(string id, HttpRequest request, CancellationToken token)
    {
        if (!TryFind(id, out var entry)) return HttpResponse.Error(404, "not_found", "There is no such transcription.");
        if (entry.Job.State != JobState.Complete) return HttpResponse.Error(409, "not_ready", "Only a finished transcription can be edited.");
        if (request.ContentLength > 8 * 1024 * 1024) return HttpResponse.Error(413, "payload_too_large", "The edits are too large.");
        // Read at most 8 MB, whether the client announced the length or sent the body in chunks.
        using var buffer = new MemoryStream();
        var piece = new byte[64 * 1024]; int read;
        while ((read = await request.Body.ReadAsync(piece, token)) > 0)
        {
            if (buffer.Length + read > 8 * 1024 * 1024) return HttpResponse.Error(413, "payload_too_large", "The edits are too large.");
            buffer.Write(piece, 0, read);
        }
        RemoteEdits? edits;
        try { edits = System.Text.Json.JsonSerializer.Deserialize<RemoteEdits>(buffer.ToArray(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); }
        catch (System.Text.Json.JsonException) { return HttpResponse.Error(400, "bad_json", "The edits are not valid JSON."); }
        if (edits?.Edits is not { } list) return HttpResponse.Error(400, "bad_json", "The edits are missing.");
        var review = (await _deps.LoadReview(entry.Id, token)).Review;
        var regions = review.Regions.ToList();
        foreach (var edit in list)
        {
            if (edit.Index < 0 || edit.Index >= regions.Count || edit.Text is null || edit.Text.Length > 50_000)
                return HttpResponse.Error(400, "bad_edit", "An edit names a region that does not exist, or its text is missing or too long.");
            regions[edit.Index] = ReviewRegion.WithEdit(regions[edit.Index], edit.Text);
        }
        await _deps.SaveReview(review with { Regions = regions }, token);
        return HttpResponse.Json(200, new { saved = list.Count });
    }

    private HttpResponse Audio(string id, HttpRequest request)
    {
        if (!TryFind(id, out var entry)) return HttpResponse.Error(404, "not_found", "There is no such transcription.");
        var kind = (request.Query.GetValueOrDefault("kind") ?? "playback").ToLowerInvariant();
        if (kind is not ("playback" or "normalized")) return HttpResponse.Error(400, "bad_kind", "kind must be playback or normalized.");
        var path = _deps.AudioPath(entry.Id, kind);
        if (path is null || !File.Exists(path)) return HttpResponse.Error(404, "not_found", "That audio is not available (yet).");
        return HttpResponse.File(path, Path.GetExtension(path).ToLowerInvariant() == ".m4a" ? "audio/mp4" : "audio/wav");
    }

    private bool TryFind(string text, out ApiJob entry)
    {
        entry = null!;
        return Guid.TryParse(text, out var id) && _jobs.TryGetValue(id, out entry!);
    }

    private async Task<HttpResponse> StatusAsync(string id, HttpRequest request, CancellationToken token)
    {
        if (!TryFind(id, out var entry)) return HttpResponse.Error(404, "not_found", "There is no such transcription.");
        if (int.TryParse(request.Query.GetValueOrDefault("wait"), out var seconds) && seconds > 0)
        {
            var until = DateTime.UtcNow.AddSeconds(Math.Min(seconds, 120));
            while (entry.Job.State is not (JobState.Complete or JobState.Failed or JobState.Cancelled) && DateTime.UtcNow < until) await Task.Delay(200, token);
        }
        return HttpResponse.Json(200, Describe(entry));
    }

    /// <summary>
    /// <c>DELETE /v1/transcriptions/{id}</c>: removes a finished recording with its transcript, edits and working files, and the copy of the recording that was
    /// uploaded. One that is still being worked on is refused; it has to be cancelled first.
    /// </summary>
    private async Task<HttpResponse> DeleteRecordingAsync(string id, CancellationToken token)
    {
        if (_deps.DeleteJob is not { } delete) return HttpResponse.Error(501, "delete_unavailable", "This server does not delete recordings.");
        if (!TryFind(id, out var entry)) return HttpResponse.Error(404, "not_found", "There is no such transcription.");
        if (entry.Fetching || entry.Running is not null || !ProjectRemoval.CanDelete(entry.Job.State))
            return HttpResponse.Error(409, "still_running", "The recording is still being worked on. Cancel it first.");
        await delete(entry.Job, token);
        _jobs.TryRemove(entry.Id, out _);
        JobDeletedByApi?.Invoke(this, entry.Id);
        return HttpResponse.Json(200, new { deleted = entry.Id });
    }

    private async Task<HttpResponse> CancelAsync(string id)
    {
        if (!TryFind(id, out var entry)) return HttpResponse.Error(404, "not_found", "There is no such transcription.");
        entry.CancelRequested = true;
        if (entry.Running is { } running) running.Cancel();
        else if (entry.Job.State == JobState.Queued)
        {
            entry.Job = entry.Job with { State = JobState.Cancelled };
            await _deps.Repository.SaveAsync(entry.Job);
            JobChangedByApi?.Invoke(this, entry.Job);
        }
        return HttpResponse.Json(200, Describe(entry));
    }

    // ---- the transcript ---------------------------------------------------------------------------------------------------------------

    private async Task<HttpResponse> TranscriptAsync(string id, HttpRequest request, CancellationToken token)
    {
        if (!TryFind(id, out var entry)) return HttpResponse.Error(404, "not_found", "There is no such transcription.");
        var format = (request.Query.GetValueOrDefault("format") ?? "json").ToLowerInvariant();
        var mode = (request.Query.GetValueOrDefault("mode") ?? "strict").ToLowerInvariant();
        if (!Formats.Contains(format)) return HttpResponse.Error(400, "bad_format", "format must be one of: " + string.Join(", ", Formats) + ".");
        if (mode is not ("strict" or "readable")) return HttpResponse.Error(400, "bad_mode", "mode must be strict or readable.");
        if (entry.Job.State != JobState.Complete)
            return HttpResponse.Error(409, "not_ready", $"The transcription is {StateName(entry.Job.State)}; the transcript exists once it is complete.");
        var transcript = await _deps.LoadTranscript(entry.Id, token);
        return await RenderFileAsync(transcript, format, mode, token);
    }

    private static string JoinedText(FinalTranscript transcript) => string.Join(" ", transcript.Regions.Select(region => region.FinalText.Trim()).Where(text => text.Length > 0));

    private async Task<HttpResponse> RenderFileAsync(FinalTranscript transcript, string format, string mode, CancellationToken token)
    {
        if (mode == "readable") transcript = TranscriptExporter.ReadableCopy(transcript);
        if (format == "json")
            return HttpResponse.Json(200, new
            {
                id = transcript.JobId, language = transcript.Language, text = JoinedText(transcript), hasTimestamps = transcript.Regions.All(region => region.NativeTimestamps),
                segments = transcript.Regions.Select((region, index) => new
                {
                    index, startMs = region.NativeTimestamps ? (long?)region.StartMs : null, endMs = region.NativeTimestamps ? (long?)region.EndMs : null,
                    text = region.FinalText, needsListening = ReviewRegion.NeedsListening(region), source = region.Source, speaker = region.Speaker
                })
            });
        var extension = format == "full-json" ? "json" : format;
        var contentType = extension switch
        {
            "json" => "application/json; charset=utf-8", "txt" => "text/plain; charset=utf-8", "md" => "text/markdown; charset=utf-8",
            "srt" => "application/x-subrip; charset=utf-8", "vtt" => "text/vtt; charset=utf-8", "csv" => "text/csv; charset=utf-8",
            _ => "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        };
        Directory.CreateDirectory(_deps.ExportFolder);
        var file = Path.Combine(_deps.ExportFolder, Guid.NewGuid().ToString("N") + "." + extension);
        try
        {
            await TranscriptExporter.SaveAsync(transcript, file, token);
            var bytes = await File.ReadAllBytesAsync(file, token);
            return HttpResponse.Bytes(200, bytes, contentType);
        }
        catch (InvalidDataException error) { return HttpResponse.Error(422, "subtitles_unavailable", error.Message); }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    // ---- for the window ---------------------------------------------------------------------------------------------------------------

    /// <summary>The addresses a client can use: this computer only, or also each address on the network.</summary>
    public static IReadOnlyList<string> Addresses(int port, bool network)
    {
        var list = new List<string> { $"http://127.0.0.1:{port}" };
        if (!network) return list;
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && item.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback))
                foreach (var address in adapter.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address)))
                    list.Add($"http://{address.Address}:{port}");
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return list;
    }
}
