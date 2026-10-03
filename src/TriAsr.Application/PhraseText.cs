using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace TriAsr.Application;

/// <summary>The text of a recognised phrase, tidied up for typing, and the sound of a phrase wrapped as a WAV file.</summary>
public static partial class PhraseText
{
    // What Whisper writes in brackets for sounds it heard: [BLANK_AUDIO], [Music], (applause), *whistling*... It is not something a person said.
    [GeneratedRegex(@"\[[^\]]*\]|♪[^♪]*♪?|\*\s?\p{L}[^*\n]{0,38}\*|\((?:[^)]*\b)?(?:music|applause|laughter|laughs|silence|noise|inaudible|crosstalk|coughs|coughing|sighs|beep|singing)\b[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Markers();

    // The credits of the subtitled videos Whisper learnt from, which it writes for sound without speech when it reads it as German, Hungarian, French...
    [GeneratedRegex(@"amara\.org|untertitelung des (?:zdf|ard|br|wdr|swr|ndr|mdr)|untertitel im auftrag des|subtitles by the|sous-titres réalisés par", RegexOptions.IgnoreCase)]
    private static partial Regex Credits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Whisper writes these for sound that holds no speech (a cough, a click, a breath): they come from the videos it learnt from.
    private static readonly HashSet<string> Phantoms = new(StringComparer.OrdinalIgnoreCase)
    {
        "you", "thank you", "thanks", "thank you very much", "thanks for watching", "thank you for watching", "bye", "bye bye", "okay", "so", "uh", "um", "hmm", "oh",
        "thanks for watching and see you in the next video", "please subscribe"
    };

    /// <summary>The text without the program's markers, in one line without the blanks Whisper puts around it.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var withoutMarkers = Markers().Replace(text, " ");
        return Whitespace().Replace(withoutMarkers, " ").Trim();
    }

    /// <summary>
    /// Whether a short phrase is probably one of the words Whisper invents for sound without speech. Only a phrase of a second or so is judged this way:
    /// "Thank you." in the middle of a sentence is real, and a long phrase is real however it ends.
    /// </summary>
    public static bool IsPhantom(string text, TimeSpan speech)
    {
        if (IsCredit(text)) return true;
        if (speech >= TimeSpan.FromMilliseconds(1200)) return false;
        var plain = new string(text.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '\'').ToArray()).Trim();
        return plain.Length == 0 || Phantoms.Contains(plain);
    }

    /// <summary>
    /// Whether the text is only the credit line of a subtitled video ("Feliratok az Amara.org közösségétől", "Untertitelung des ZDF, 2020"), which Whisper writes
    /// for sound without speech. However long the sound was, nobody said it. A long text that mentions such a credit is not judged this way.
    /// </summary>
    public static bool IsCredit(string text) => Credits().IsMatch(text) && Whitespace().Split(text.Trim()).Length <= 12;

    /// <summary>Chinese, Japanese and Korean (and the full-width forms) are written without a space between words and phrases.</summary>
    public static bool IsUnspacedScript(char c) =>
        c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯' or '。' or '、';

    /// <summary>
    /// Puts the words of a phrase after the text of a note: with a space between them, except where the text is empty or already ends in a space or a line break,
    /// or where either side is written without spaces.
    /// </summary>
    public static string Append(string text, string words)
    {
        if (words.Length == 0) return text;
        if (text.Length == 0 || char.IsWhiteSpace(text[^1]) || IsUnspacedScript(text[^1]) || IsUnspacedScript(words[0])) return text + words;
        return text + " " + words;
    }

    /// <summary>A WAV file (16 kHz, mono, 16-bit) around the sound of a phrase.</summary>
    public static byte[] Wav(ReadOnlySpan<byte> pcm)
    {
        var file = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(36 + pcm.Length));
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), 16);
        "data"u8.CopyTo(file.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), (uint)pcm.Length);
        pcm.CopyTo(file.AsSpan(44));
        return file;
    }
}
