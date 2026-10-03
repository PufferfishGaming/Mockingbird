using TriAsr.Domain;

namespace TriAsr.Application;

/// <summary>The words of one phrase and the language they are written in (empty when it is not known).</summary>
public sealed record LivePhrase(string Text, string Language = "");

/// <summary>
/// Reads one spoken phrase for live dictation (ADR: live dictation). Where it runs is not the caller's business: on this computer's speech program, or on
/// a server that a Client is connected to.
/// </summary>
public interface ILiveRecognizer
{
    /// <param name="wav">The phrase as a WAV file (16 kHz, mono, 16-bit).</param>
    /// <param name="language"><c>auto</c>, a language code, or two joined with <c>+</c> (<c>en+hu</c>): the person speaks both and may switch between them (<see cref="LiveLanguagePicker"/>).</param>
    /// <param name="recent">The language the previous phrase was in, or null. With two languages, a phrase that reads about as well in both is taken to be in this one.</param>
    /// <returns>What was said, cleaned of the program's own markers (empty when nothing could be made out), and the language it is in.</returns>
    /// <exception cref="LiveException">The phrase could not be read. The message says why, in plain words (it is in English and is translated by the app).</exception>
    Task<LivePhrase> RecognizeAsync(byte[] wav, string language, string? recent, CancellationToken token);
}

/// <summary>A phrase could not be read: the speech model is missing, the program failed, the server did not answer.</summary>
public sealed class LiveException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The sentences live dictation says to a person from the layers below the interface; each is listed in <c>extra-keys.json</c> and translated by the app.</summary>
public static class LiveMessages
{
    public const string NoModel = "No speech model is downloaded yet. Download one on the Models page.";
    public const string Failed = "The phrase could not be recognised.";
    public const string TooLong = "The phrase is too long.";

    public static readonly IReadOnlyList<string> All = [NoModel, Failed, TooLong];
}

/// <summary>What the speech program made of a phrase in one language.</summary>
/// <param name="Language">The language it read the phrase in (the one it detected, when it was not told).</param>
/// <param name="Detection">How sure it was of that language when it detected it itself, 0 to 1; 1 when it was told the language.</param>
/// <param name="Confidence">The average logarithm of the probabilities of its words' tokens: near 0 for speech read in the language it was spoken in, far below for speech read in another.</param>
public sealed record SpeechReading(string Language, double Detection, double Confidence, string Text)
{
    /// <summary>What a reading without words scores, so that it never wins against one with words.</summary>
    public const double NoConfidence = -99;
}

/// <summary>
/// Decides which of two languages a phrase was spoken in (ADR: live dictation). Whisper decides the language from the sound alone, and it does so badly for a person
/// with an accent: measured on a Hungarian speaker's English, every phrase was heard as Hungarian (84 to 98 % sure) and written as a Hungarian translation.
/// Told the language, it translates whatever else is said into it. So when the person names the two languages they speak:
/// <list type="number">
/// <item>the phrase is read as usual, and taken as it is when the program is all but certain (<see cref="SureDetection"/>) of one of the two languages;</item>
/// <item>otherwise it is read in the other language too, and the reading whose words the program is surer of wins: speech read in its own language reads with confidence,
/// a translation does not;</item>
/// <item>a close call (within <see cref="SwitchMargin"/>) goes to the language of the previous phrase, because people do not switch every sentence.</item>
/// </list>
/// On the measured phrases (native English and German, the Hungarian speaker's English and Hungarian, and a recording that switches) this got 183 of 189 right where
/// detection alone got 163, and none worse; it reads about half the phrases twice.
/// </summary>
public static class LiveLanguagePicker
{
    /// <summary>At or above this the detected language is taken without a second reading. The accented English that was misheard came below 0.98; Hungarian was 0.998 and above.</summary>
    public const double SureDetection = 0.99;

    /// <summary>How much better the other language has to read to win over the language of the previous phrase.</summary>
    public const double SwitchMargin = 0.10;

    /// <summary>Reads a phrase in the language (or languages) chosen.</summary>
    /// <param name="read">Reads the phrase in one language code, or <c>auto</c> to let the program detect it.</param>
    /// <param name="language">As <see cref="ILiveRecognizer.RecognizeAsync"/> takes it; a choice that cannot be read is taken as <c>auto</c>.</param>
    public static async Task<LivePhrase> PickAsync(Func<string, CancellationToken, Task<SpeechReading>> read, string language, string? recent, CancellationToken token)
    {
        if (!LanguageCatalog.TryParseChoice(language, out var codes)) codes = [];
        if (codes.Count <= 1)
        {
            var only = await read(codes.Count == 0 ? "auto" : codes[0], token).ConfigureAwait(false);
            return new(PhraseText.Clean(only.Text), only.Language);
        }
        var first = await read("auto", token).ConfigureAwait(false);
        if (IsSure(first.Language, first.Detection, codes)) return new(PhraseText.Clean(first.Text), first.Language);
        var readings = new List<SpeechReading>();
        if (codes.Contains(first.Language)) readings.Add(first);
        foreach (var code in codes.Where(code => code != first.Language)) readings.Add(await read(code, token).ConfigureAwait(false));
        return Choose(readings, recent) is { } best ? new(PhraseText.Clean(best.Text), best.Language) : new("", "");
    }

    /// <summary>Whether the language the program detected can be taken without reading the phrase in the other language too.</summary>
    public static bool IsSure(string detected, double detection, IReadOnlyList<string> codes) => codes.Contains(detected) && detection >= SureDetection;

    /// <summary>
    /// The reading whose words the program was surest of, among those that hold words (not only an invented subtitle credit); a close call goes to the
    /// <paramref name="recent"/> language. Null when none holds words.
    /// </summary>
    public static SpeechReading? Choose(IEnumerable<SpeechReading> readings, string? recent)
    {
        var usable = readings.Where(reading => PhraseText.Clean(reading.Text) is { Length: > 0 } text && !PhraseText.IsCredit(text)).ToList();
        if (usable.Count == 0) return null;
        var best = usable.MaxBy(reading => reading.Confidence)!;
        if (recent is not null && best.Language != recent && usable.FirstOrDefault(reading => reading.Language == recent) is { } kept && best.Confidence < kept.Confidence + SwitchMargin) best = kept;
        return best;
    }
}

/// <summary>What the speech program said about the language of one stretch of a recording when it detected it.</summary>
/// <param name="Index">The stretch's number in the chunk plan.</param>
public sealed record ChunkDetection(int Index, string Language, double Detection);

/// <summary>
/// A recording in two languages (ADR: language pairs): the language of each speech stretch of its chunk plan is decided by the same rule as a phrase of live dictation
/// (<see cref="LiveLanguagePicker"/>), and the recording is divided into <see cref="LanguageBlock"/>s that are each read in their own language. Measured on recordings
/// that switch between English and Hungarian, this brought the word error rate from 66-81 % (the whole file read in the language detected) to 7-9 %; a recording in
/// one language comes out exactly as it would with that language chosen.
/// </summary>
public static class LanguageBlocks
{
    /// <summary>
    /// The language of every speech stretch, in the order of the recording. A stretch the program was sure of is taken as detected; for the others,
    /// <paramref name="readings"/> holds a reading in each language of the choice, and the rule of live dictation decides, the previous stretch's language settling a close call.
    /// A stretch where no reading holds words takes the language of the one before it (or the first of the choice).
    /// </summary>
    public static IReadOnlyDictionary<int, string> Decide(IReadOnlyList<ChunkDetection> detections, IReadOnlyDictionary<int, IReadOnlyList<SpeechReading>> readings, IReadOnlyList<string> codes)
    {
        var languages = new Dictionary<int, string>();
        string? recent = null;
        foreach (var detection in detections)
        {
            string? language;
            if (LiveLanguagePicker.IsSure(detection.Language, detection.Detection, codes)) language = detection.Language;
            else language = readings.TryGetValue(detection.Index, out var read) ? LiveLanguagePicker.Choose(read, recent)?.Language : null;
            if (language is not null) recent = language;
            languages[detection.Index] = language ?? recent ?? codes[0];
        }
        return languages;
    }

    /// <summary>
    /// Divides the recording into blocks of one language: a speech stretch belongs to its language, a quiet one to its neighbours (split in the middle between two
    /// different ones), and neighbours of one language are joined. A recording without speech is one block in <paramref name="fallback"/>.
    /// </summary>
    public static IReadOnlyList<LanguageBlock> From(ChunkPlan plan, IReadOnlyDictionary<int, string> languages, string fallback)
    {
        var speech = plan.Chunks.Where(chunk => chunk.IsSpeech && languages.ContainsKey(chunk.Index)).ToList();
        if (speech.Count == 0) return [new(0, plan.DurationMs, fallback)];
        var pieces = new List<LanguageBlock>();
        foreach (var chunk in plan.Chunks)
        {
            if (chunk.IsSpeech && languages.TryGetValue(chunk.Index, out var own)) { pieces.Add(new(chunk.StartMs, chunk.EndMs, own)); continue; }
            var before = speech.LastOrDefault(item => item.EndMs <= chunk.StartMs);
            var after = speech.FirstOrDefault(item => item.StartMs >= chunk.EndMs);
            var (left, right) = (before is null ? null : languages[before.Index], after is null ? null : languages[after.Index]);
            if (left is not null && right is not null && left != right)
            {
                var middle = (chunk.StartMs + chunk.EndMs) / 2;
                pieces.Add(new(chunk.StartMs, middle, left)); pieces.Add(new(middle, chunk.EndMs, right));
            }
            else pieces.Add(new(chunk.StartMs, chunk.EndMs, left ?? right ?? fallback));
        }
        var blocks = new List<LanguageBlock>();
        foreach (var piece in pieces)
        {
            var start = blocks.Count > 0 ? Math.Max(piece.StartMs, blocks[^1].EndMs) : 0;                // overlapping stretches (speech cut without a pause) are not read twice
            if (blocks.Count > 0 && blocks[^1].Language == piece.Language) blocks[^1] = blocks[^1] with { EndMs = Math.Max(blocks[^1].EndMs, piece.EndMs) };
            else if (piece.EndMs > start) blocks.Add(piece with { StartMs = start });
        }
        blocks[^1] = blocks[^1] with { EndMs = plan.DurationMs };
        return blocks;
    }

    /// <summary>A plan for a recording that the speech detector could not look at: stretches of about 20 s, cut wherever they fall.</summary>
    public static ChunkPlan Even(long durationMs) => ChunkPlanner.Plan(durationMs, [new SpeechSpan(0, durationMs)], ChunkOptions.ForCanary, "even");

    /// <summary>Canary's windows for one language: the plan's speech stretches inside that language's blocks, cut at the block edges.</summary>
    public static IReadOnlyList<CanaryWindow> Windows(ChunkPlan plan, IReadOnlyList<LanguageBlock> blocks, string language)
    {
        var windows = new List<CanaryWindow>();
        foreach (var chunk in plan.SpeechChunks())
            foreach (var block in blocks.Where(block => block.Language == language))
            {
                var (start, end) = (Math.Max(chunk.StartMs, block.StartMs), Math.Min(chunk.EndMs, block.EndMs));
                if (end - start >= 100) windows.Add(new(chunk.Index, start, end, true, chunk.OverlapsPrevious && start == chunk.StartMs && windows.Count > 0));
            }
        return windows;
    }

    /// <summary>The languages that have blocks, in the order of the choice: one when the recording turned out to be in one language only.</summary>
    public static IReadOnlyList<string> Present(IReadOnlyList<LanguageBlock> blocks, IReadOnlyList<string> codes) => codes.Where(code => blocks.Any(block => block.Language == code)).ToArray();
}
