using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using TriAsr.Application;
using TriAsr.Audio.Live;
using TriAsr.Audio.Recording;

namespace TriAsr.App;

/// <summary>What is done with the words of a phrase once they are read: typed where the cursor is, or added to a note. It is called on a background thread, for one phrase at a time and in the order the phrases were spoken.</summary>
/// <exception cref="InvalidOperationException">Thrown when the words could not be delivered (Windows did not take the keys); the message is shown to the person.</exception>
public delegate void PhraseHandler(string text);

/// <summary>
/// The listening that live dictation and live notes have in common (ADR: live dictation). The microphone's sound is cut into phrases at the pauses
/// (<see cref="UtteranceDetector"/>), each phrase is read by <see cref="ILiveRecognizer"/> (on this computer, or on the server a Client is connected to) and its
/// words go to a <see cref="PhraseHandler"/>. One phrase is read at a time and handed on in the order it was spoken, so that a slow phrase never overtakes a quick one.
/// The events are raised on the window's thread, through the <c>onUi</c> function the session was made with.
/// </summary>
public sealed class LiveSession(IMicrophone microphone, Action<Action> onUi) : IDisposable
{
    private readonly object _gate = new();
    private IDisposable? _capture;
    private UtteranceDetector? _detector;
    private Channel<Utterance>? _phrases;
    private Task _consumer = Task.CompletedTask;
    private CancellationTokenSource? _cancellation;
    private int _waiting;
    private volatile bool _problem;
    private (string Language, DateTime At)? _recent;

    /// <summary>After this long without a phrase the language of the last one is forgotten: the person may have gone on to the other language, or be someone else.</summary>
    public static readonly TimeSpan RecentLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The language the last phrase was written in, while it is recent; with a choice of two languages it decides a phrase that reads about as well in both.</summary>
    public string? RecentLanguage => _recent is { } recent && DateTime.UtcNow - recent.At < RecentLifetime ? recent.Language : null;

    public bool IsListening { get; private set; }

    /// <summary>A problem is on show; it stays until the next phrase is read.</summary>
    public bool HasProblem => _problem;

    /// <summary>How many phrases are being read or are waiting to be.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>How loud the microphone is now, 0 to 100, for a level meter.</summary>
    public double Level
    {
        get
        {
            UtteranceDetector? detector;
            lock (_gate) detector = _detector;
            return detector is null ? 0 : Math.Min(100, detector.Level * 600);
        }
    }

    /// <summary>A phrase began to be read.</summary>
    public event Action? Reading;

    /// <summary>All the phrases were read and nothing is wrong: the session is just listening again.</summary>
    public event Action? Settled;

    /// <summary>A phrase could not be read or delivered; the text says why (built again after a language change).</summary>
    public event Action<Func<string>>? Problem;

    /// <summary>The microphone stopped working while listening (unplugged, taken by another program). The owner stops the session.</summary>
    public event Action<Func<string>>? MicrophoneFailed;

    /// <summary>Starts listening.</summary>
    /// <param name="device">The microphone, or <see cref="WindowsMicrophone.DefaultDevice"/>.</param>
    /// <param name="language">The language to read with (<c>auto</c>, a code, or two joined with <c>+</c>); asked for every phrase, so a change takes effect at once.</param>
    /// <exception cref="MicrophoneException">The microphone could not be opened; the message says why.</exception>
    public async Task StartAsync(int device, ILiveRecognizer recognizer, Func<string> language, PhraseHandler deliver)
    {
        if (IsListening) return;
        await _consumer.ConfigureAwait(true);                         // a stop that is still reading its last phrase has to finish first
        var phrases = Channel.CreateUnbounded<Utterance>(new UnboundedChannelOptions { SingleReader = true });
        var detector = new UtteranceDetector(phrase => phrases.Writer.TryWrite(phrase));
        var cancellation = new CancellationTokenSource();
        try { _capture = microphone.Start(device, piece => detector.Feed(piece.Span), error => { var reason = error.Message; onUi(() => MicrophoneFailed?.Invoke(() => Loc.Describe(reason))); }); }
        catch
        {
            cancellation.Dispose();
            throw;
        }
        lock (_gate) { _detector = detector; _phrases = phrases; _cancellation = cancellation; }
        _consumer = Task.Run(() => ReadAndDeliverAsync(recognizer, language, deliver, phrases.Reader, cancellation.Token));
        _problem = false;
        IsListening = true;
    }

    /// <summary>
    /// Stops listening. What was being said is still read and delivered, so nothing is lost; this returns when the last phrase has been dealt with.
    /// </summary>
    /// <param name="finishing">Called first when phrases are still being read, so that the owner can say so.</param>
    public async Task StopAsync(Action? finishing = null)
    {
        Channel<Utterance>? phrases;
        UtteranceDetector? detector;
        lock (_gate) { phrases = _phrases; detector = _detector; _phrases = null; _detector = null; }
        if (!IsListening && phrases is null) { await _consumer.ConfigureAwait(true); return; }
        IsListening = false;
        _capture?.Dispose();                                          // waits for the last piece of sound
        _capture = null;
        detector?.Flush();
        phrases?.Writer.TryComplete();
        if (Volatile.Read(ref _waiting) > 0) finishing?.Invoke();
        await _consumer.ConfigureAwait(true);
        _cancellation?.Dispose(); _cancellation = null;
    }

    private async Task ReadAndDeliverAsync(ILiveRecognizer recognizer, Func<string> language, PhraseHandler deliver, ChannelReader<Utterance> phrases, CancellationToken token)
    {
        try
        {
            await foreach (var phrase in phrases.ReadAllAsync(token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _waiting);
                _problem = false;
                onUi(() => { if (IsListening) Reading?.Invoke(); });
                try { await ReadOneAsync(recognizer, language(), deliver, phrase, token).ConfigureAwait(false); }
                finally
                {
                    var left = Interlocked.Decrement(ref _waiting);
                    onUi(() => { if (IsListening && left == 0 && !_problem) Settled?.Invoke(); });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadOneAsync(ILiveRecognizer recognizer, string language, PhraseHandler deliver, Utterance phrase, CancellationToken token)
    {
        string text, spoken;
        try
        {
            var read = await recognizer.RecognizeAsync(PhraseText.Wav(phrase.Pcm), language, RecentLanguage, token).ConfigureAwait(false);
            (text, spoken) = (PhraseText.Clean(read.Text), read.Language);
        }
        catch (OperationCanceledException) { throw; }
        catch (LiveException error) { var reason = error.Message; Fail(() => Loc.Describe(reason)); return; }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            var reason = error.Message;
            Fail(() => Loc.T("The phrase could not be recognised: {0}", Loc.Describe(reason)));
            return;
        }
        if (text.Length == 0 || PhraseText.IsPhantom(text, phrase.Speech)) return;
        if (spoken.Length > 0) _recent = (spoken, DateTime.UtcNow);
        try { deliver(text); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            var reason = error.Message;
            Fail(() => reason);
        }
    }

    private void Fail(Func<string> message)
    {
        _problem = true;
        onUi(() => Problem?.Invoke(message));
    }

    /// <summary>The window is closing: the microphone is let go of and what is still being read is dropped. (Nothing waits here: the window's own thread must stay free.)</summary>
    public void Dispose()
    {
        Channel<Utterance>? phrases;
        lock (_gate) { phrases = _phrases; _phrases = null; _detector = null; }
        _capture?.Dispose(); _capture = null;
        phrases?.Writer.TryComplete();
        _cancellation?.Cancel();
    }
}
