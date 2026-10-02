using System.Buffers.Binary;
using TriAsr.Audio.Recording;

namespace TriAsr.Audio.Tests;

public sealed class RecordingTests
{
    /// <summary>A microphone that hands over prepared sound when told to, so that a recording can be tested without sound in the room.</summary>
    private sealed class FakeMicrophone : IMicrophone
    {
        private Action<ReadOnlyMemory<byte>>? _onData;
        private Action<Exception>? _onFailure;
        public int? StartedWith { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? FailToStart { get; set; }

        public IReadOnlyList<InputDevice> Devices() => [new(WindowsMicrophone.DefaultDevice, ""), new(0, "Fake microphone")];

        public IDisposable Start(int deviceId, Action<ReadOnlyMemory<byte>> onData, Action<Exception> onFailure)
        {
            if (FailToStart is not null) throw FailToStart;
            StartedWith = deviceId; _onData = onData; _onFailure = onFailure;
            return new Stop(this);
        }

        public void Hear(params short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
            _onData!(bytes);
        }

        public void HearSeconds(double seconds, short value)
        {
            var samples = new short[(int)(seconds * WindowsMicrophone.SampleRate)];
            Array.Fill(samples, value);
            Hear(samples);
        }

        public void Unplug(Exception error) => _onFailure!(error);

        private sealed class Stop(FakeMicrophone owner) : IDisposable { public void Dispose() => owner.Disposed = true; }
    }

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WhatIsHeardEndsUpInAWavFileThatTheRestOfTheProgramCanRead()
    {
        var folder = NewFolder();
        try
        {
            var microphone = new FakeMicrophone();
            using var session = new RecordingSession(microphone, folder);
            session.Start(WindowsMicrophone.DefaultDevice);
            Assert.True(session.IsRecording);
            Assert.Equal(WindowsMicrophone.DefaultDevice, microphone.StartedWith);
            microphone.HearSeconds(1.5, 1000);
            microphone.HearSeconds(0.5, -2000);
            Assert.Equal(2.0, session.Duration.TotalSeconds, 3);

            var recorded = session.Stop();
            Assert.True(microphone.Disposed);
            Assert.False(session.IsRecording);
            Assert.False(recorded.Silent);
            Assert.Equal(2.0, recorded.Duration.TotalSeconds, 3);
            Assert.Equal(44 + 2 * 16000 * 2, recorded.Bytes);
            Assert.Equal(recorded.Bytes, new FileInfo(recorded.Path).Length);
            Assert.Matches(@"^Recording \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\.wav$", Path.GetFileName(recorded.Path));

            var info = WaveAudio.Inspect(recorded.Path);            // the same reader the pipeline's audio checks use
            Assert.Equal(2 * 16000, info.SampleCount);
            Assert.Equal(16000, info.SampleRate);
            var samples = WaveAudio.ReadSamples(recorded.Path);
            Assert.Equal(1000 / 32768f, samples[100], 4);
            Assert.Equal(-2000 / 32768f, samples[^100], 4);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void TheLevelFollowsTheLoudestSampleOfTheLatestPiece()
    {
        var folder = NewFolder();
        try
        {
            var microphone = new FakeMicrophone();
            using var session = new RecordingSession(microphone, folder);
            session.Start(0);
            Assert.Equal(0, session.Level);
            microphone.Hear(0, 16384, -8000, 100);
            Assert.Equal(16384 / 32767.0, session.Level, 4);
            microphone.Hear(short.MinValue);                         // the quietest value to negate must not overflow
            Assert.Equal(1.0, session.Level, 4);
            microphone.Hear(0, 0, 0);
            Assert.Equal(0, session.Level);
            session.Discard();
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void ARecordingOfPureSilenceIsSaidToBeSilentSoThatABlockedMicrophoneIsNoticed()
    {
        var folder = NewFolder();
        try
        {
            var microphone = new FakeMicrophone();
            using var session = new RecordingSession(microphone, folder);
            session.Start(0);
            microphone.HearSeconds(0.5, 0);
            Assert.False(session.HeardNothing);                       // too soon to say
            microphone.HearSeconds(1.0, 0);
            Assert.True(session.HeardNothing);                        // a second and a half of exact zeros: Windows is not giving us the sound
            Assert.True(session.Stop().Silent);

            using var second = new RecordingSession(microphone, folder);
            second.Start(0);
            microphone.HearSeconds(1.0, 0);
            microphone.Hear(5);                                       // one sample of real sound is enough to not be silent
            Assert.False(second.HeardNothing);
            Assert.False(second.Stop().Silent);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void DiscardingDeletesTheFileAndRecordingsDoNotOverwriteEachOther()
    {
        var folder = NewFolder();
        try
        {
            var microphone = new FakeMicrophone();
            using var first = new RecordingSession(microphone, folder);
            first.Start(0);
            var firstPath = first.Path!;
            using var second = new RecordingSession(microphone, folder);
            second.Start(0);                                          // in the same second: another name
            Assert.NotEqual(firstPath, second.Path);
            microphone.HearSeconds(0.2, 1);
            first.Discard();
            Assert.False(File.Exists(firstPath));
            Assert.True(File.Exists(second.Path));
            Assert.Throws<InvalidOperationException>(() => first.Stop());
            second.Stop();
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void AMicrophoneThatCannotBeOpenedLeavesNoFileBehindAndSaysWhy()
    {
        var folder = NewFolder();
        try
        {
            var microphone = new FakeMicrophone { FailToStart = new MicrophoneException("No microphone was found.") };
            using var session = new RecordingSession(microphone, folder);
            var error = Assert.Throws<MicrophoneException>(() => session.Start(0));
            Assert.Equal("No microphone was found.", error.Message);
            Assert.False(session.IsRecording);
            Assert.Null(session.Path);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void WhenTheMicrophoneIsUnpluggedTheSoundSoFarIsKept()
    {
        var folder = NewFolder();
        try
        {
            var microphone = new FakeMicrophone();
            using var session = new RecordingSession(microphone, folder);
            Exception? told = null;
            session.Failed += error => told = error;
            session.Start(0);
            microphone.HearSeconds(1, 300);
            microphone.Unplug(new MicrophoneException("The microphone was disconnected."));
            Assert.Equal("The microphone was disconnected.", told?.Message);
            Assert.Equal("The microphone was disconnected.", session.Failure?.Message);
            var recorded = session.Stop();
            Assert.Equal(1.0, recorded.Duration.TotalSeconds, 3);
            Assert.Equal(16000, WaveAudio.Inspect(recorded.Path).SampleCount);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void TheHeaderIsAValidWavHeaderForAnyLength()
    {
        using var stream = new MemoryStream();
        RecordingSession.WriteHeader(stream, 12345);
        var bytes = stream.ToArray();
        Assert.Equal(44, bytes.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(36u + 12345, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(bytes, 8, 8));
        Assert.Equal(16000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(32000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
        Assert.Equal(12345u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    [Fact]
    public void EachKindOfWindowsErrorHasAnExplanationAPersonCanActOn()
    {
        Assert.Contains("No microphone", WindowsMicrophone.Explain(2));
        Assert.Contains("another program", WindowsMicrophone.Explain(4));
        Assert.Contains("format", WindowsMicrophone.Explain(32));
        Assert.Contains("Windows error 99", WindowsMicrophone.Explain(99));
    }

    /// <summary>
    /// The real microphone, for one second. It is off unless TRIASR_TEST_MICROPHONE=1, because it listens to the room: a test run must not do that on its own.
    /// It checks that Windows opens the device, delivers about a second of 16 kHz sound in pieces, and lets go of the device again.
    /// </summary>
    [Fact]
    public void TheRealMicrophoneDeliversAboutASecondOfSoundWhenAsked()
    {
        if (Environment.GetEnvironmentVariable("TRIASR_TEST_MICROPHONE") != "1") return;
        var microphone = new WindowsMicrophone();
        var devices = microphone.Devices();
        if (devices.Count == 0) return;                     // a computer without a microphone
        var folder = NewFolder();
        try
        {
            using var session = new RecordingSession(microphone, folder);
            session.Start(WindowsMicrophone.DefaultDevice);
            Thread.Sleep(1200);
            var recorded = session.Stop();
            Assert.InRange(recorded.Duration.TotalSeconds, 0.9, 1.5);
            Assert.Equal(16000, WaveAudio.Inspect(recorded.Path).SampleRate);
            using var again = new RecordingSession(microphone, folder);
            again.Start(WindowsMicrophone.DefaultDevice);   // the device was let go of: it opens again
            again.Discard();
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
