using System.Buffers.Binary;

namespace TriAsr.Audio.Live;

/// <summary>One spoken phrase cut out of the microphone's sound: 16 kHz, mono, 16-bit PCM without a WAV header.</summary>
/// <param name="Duration">The length of <paramref name="Pcm"/>, with a little silence kept before and after the words.</param>
/// <param name="Speech">How much of it was louder than the room.</param>
public sealed record Utterance(byte[] Pcm, TimeSpan Duration, TimeSpan Speech);

/// <summary>How the detector tells a spoken phrase from the room. The defaults suit dictation into a headset or a laptop microphone.</summary>
/// <param name="StartLevel">The quietest sound (RMS, 0 to 1) that can count as speech, however quiet the room is.</param>
/// <param name="NoiseMultiplier">Speech has to be this many times louder than the room's own noise.</param>
/// <param name="StartMs">How long the sound has to stay loud before it counts as the start of a phrase (so that a click or a cough does not).</param>
/// <param name="PreRollMs">How much sound from before the start is kept, so that a soft first word is not cut off.</param>
/// <param name="EndSilenceMs">How long a pause ends a phrase.</param>
/// <param name="TailMs">How much of that pause is kept at the end of the phrase.</param>
/// <param name="MinSpeechMs">A phrase with less speech than this is thrown away (a cough, a tap on the desk).</param>
/// <param name="MaxMs">The longest phrase handed on; a longer stretch of speech is cut at its quietest moment (Whisper reads 30 seconds at a time).</param>
public sealed record UtteranceOptions(double StartLevel = 0.012, double NoiseMultiplier = 3.0, int StartMs = 80, int PreRollMs = 300, int EndSilenceMs = 700,
    int TailMs = 250, int MinSpeechMs = 300, int MaxMs = 25_000);

/// <summary>
/// Cuts the microphone's stream into phrases (ADR: live dictation). It listens for sound that is clearly louder than the room, collects it with a little
/// before and after, and hands the phrase on when the speaker pauses. The room's noise is learnt as it goes, so a fan or a distant street does not start a phrase.
/// It only looks at loudness: deciding what was said, and whether it was speech at all, is for the speech program.
/// </summary>
public sealed class UtteranceDetector(Action<Utterance> onUtterance, UtteranceOptions? options = null)
{
    private const int SampleRate = 16_000;
    private const int FrameMs = 20;
    private const int FrameBytes = SampleRate / 1000 * FrameMs * 2;
    private const double FloorStart = 0.004;
    private const int CalibrationFrames = 5;

    private sealed record Frame(byte[] Pcm, double Rms, bool Loud);

    private readonly UtteranceOptions _options = options ?? new();
    private readonly object _gate = new();
    private readonly byte[] _partial = new byte[FrameBytes];
    private int _partialBytes;
    private readonly Queue<Frame> _preRoll = new();
    private readonly List<Frame> _frames = [];
    private int _loudRun;
    private int _quietRun;
    private bool _speaking;
    private double _floor = FloorStart;
    private int _calibrated;
    private double _calibrationMinimum = double.MaxValue;
    private double _level;

    /// <summary>The loudness of the latest 20 ms (RMS, 0 to 1), for a level meter.</summary>
    public double Level { get { lock (_gate) return _level; } }

    /// <summary>Whether a phrase is being collected right now.</summary>
    public bool InSpeech { get { lock (_gate) return _speaking; } }

    /// <summary>The sound that counts as speech now: the room's noise times the multiplier, but never less than <see cref="UtteranceOptions.StartLevel"/>.</summary>
    public double Threshold { get { lock (_gate) return Math.Max(_options.StartLevel, _floor * _options.NoiseMultiplier); } }

    /// <summary>Takes sound of any length; a phrase that is complete is handed to the callback (on the calling thread, after the detector's lock is released).</summary>
    public void Feed(ReadOnlySpan<byte> pcm)
    {
        List<Utterance>? ready = null;
        lock (_gate)
        {
            while (pcm.Length > 0)
            {
                var take = Math.Min(FrameBytes - _partialBytes, pcm.Length);
                pcm[..take].CopyTo(_partial.AsSpan(_partialBytes));
                _partialBytes += take; pcm = pcm[take..];
                if (_partialBytes < FrameBytes) break;
                _partialBytes = 0;
                Step(_partial.ToArray(), ref ready);
            }
        }
        if (ready is not null) foreach (var utterance in ready) onUtterance(utterance);
    }

    /// <summary>The dictation ends: a phrase that is half-way is handed on, and the detector is ready for the next time.</summary>
    public void Flush()
    {
        List<Utterance>? ready = null;
        lock (_gate)
        {
            if (_speaking) Finish(ref ready);
            _partialBytes = 0; _preRoll.Clear(); _frames.Clear();
            _loudRun = 0; _quietRun = 0; _speaking = false; _level = 0;
        }
        if (ready is not null) foreach (var utterance in ready) onUtterance(utterance);
    }

    private static double RmsOf(ReadOnlySpan<byte> frame)
    {
        double sum = 0;
        var samples = frame.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(frame[(i * 2)..]) / 32768.0;
            sum += sample * sample;
        }
        return samples == 0 ? 0 : Math.Sqrt(sum / samples);
    }

    private void Step(byte[] pcm, ref List<Utterance>? ready)
    {
        var rms = RmsOf(pcm);
        _level = rms;
        // The first moments teach the detector how loud the room is (the quietest of them: someone may already be talking). Nothing starts a phrase
        // before that is done, but the sound is kept in the pre-roll, so that a word spoken at once is not lost.
        var calibrating = _calibrated < CalibrationFrames;
        if (calibrating)
        {
            _calibrationMinimum = Math.Min(_calibrationMinimum, rms);
            if (++_calibrated == CalibrationFrames) _floor = Math.Clamp(_calibrationMinimum, 0.0005, 0.05);
        }
        var threshold = Math.Max(_options.StartLevel, _floor * _options.NoiseMultiplier);
        var loud = !calibrating && rms >= threshold;
        // Inside a phrase a dip to 70% of the threshold is still the same word; the room's noise is only learnt from sound that is not speech.
        var holds = rms >= threshold * 0.7;
        var frame = new Frame(pcm, rms, loud);
        if (!_speaking)
        {
            if (!loud && _calibrated >= CalibrationFrames) _floor = Math.Clamp(_floor * 0.95 + rms * 0.05, 0.0005, 0.05);
            _preRoll.Enqueue(frame);
            var keep = (_options.PreRollMs + _options.StartMs) / FrameMs + 1;
            while (_preRoll.Count > keep) _preRoll.Dequeue();
            _loudRun = loud ? _loudRun + 1 : 0;
            if (_loudRun >= Math.Max(1, _options.StartMs / FrameMs))
            {
                _speaking = true; _quietRun = 0;
                _frames.AddRange(_preRoll);
                _preRoll.Clear();
            }
            return;
        }
        _frames.Add(frame);
        _quietRun = holds ? 0 : _quietRun + 1;
        if (_quietRun * FrameMs >= _options.EndSilenceMs) { Finish(ref ready); return; }
        if (_frames.Count * FrameMs >= _options.MaxMs) Split(ref ready);
    }

    /// <summary>The phrase ends at a pause: the silence after the words is cut down to a short tail.</summary>
    private void Finish(ref List<Utterance>? ready)
    {
        var keepSilence = _options.TailMs / FrameMs;
        var trailing = 0;
        for (var i = _frames.Count - 1; i >= 0 && !_frames[i].Loud; i--) trailing++;
        if (trailing > keepSilence) _frames.RemoveRange(_frames.Count - (trailing - keepSilence), trailing - keepSilence);
        Emit(_frames, ref ready);
        _frames.Clear(); _speaking = false; _loudRun = 0; _quietRun = 0;
    }

    /// <summary>A phrase that goes on for too long is cut at its quietest moment in the last two seconds; the rest is the start of the next phrase.</summary>
    private void Split(ref List<Utterance>? ready)
    {
        var from = Math.Max(1, _frames.Count - 2000 / FrameMs);
        var cut = from;
        for (var i = from; i < _frames.Count; i++) if (_frames[i].Rms < _frames[cut].Rms) cut = i;
        var first = _frames.GetRange(0, cut + 1);
        var rest = _frames.GetRange(cut + 1, _frames.Count - cut - 1);
        Emit(first, ref ready);
        _frames.Clear(); _frames.AddRange(rest);
        _quietRun = 0;
    }

    private void Emit(List<Frame> frames, ref List<Utterance>? ready)
    {
        var speechFrames = frames.Count(frame => frame.Loud);
        if (speechFrames * FrameMs < _options.MinSpeechMs) return;
        var bytes = new byte[frames.Count * FrameBytes];
        for (var i = 0; i < frames.Count; i++) frames[i].Pcm.CopyTo(bytes, i * FrameBytes);
        (ready ??= []).Add(new Utterance(bytes, TimeSpan.FromMilliseconds(frames.Count * FrameMs), TimeSpan.FromMilliseconds(speechFrames * FrameMs)));
    }
}
