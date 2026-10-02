using System.Buffers.Binary;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Audio.Recording;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The recorder of the New transcription page and of the Client (ADR-0016), without a microphone: a stand-in hears what the test tells it to.</summary>
public sealed class RecorderTests
{
    private sealed class Room : IMicrophone
    {
        private Action<ReadOnlyMemory<byte>>? _onData;
        private Action<Exception>? _onFailure;
        public List<InputDevice> Present { get; } = [new(WindowsMicrophone.DefaultDevice, ""), new(0, "Desk microphone")];
        public Exception? CannotOpen { get; set; }
        public int? Opened { get; private set; }

        public IReadOnlyList<InputDevice> Devices() => Present.Count == 1 ? [] : Present;

        public IDisposable Start(int deviceId, Action<ReadOnlyMemory<byte>> onData, Action<Exception> onFailure)
        {
            if (CannotOpen is not null) throw CannotOpen;
            Opened = deviceId; _onData = onData; _onFailure = onFailure;
            return new Handle();
        }

        public void Say(double seconds, short value)
        {
            var bytes = new byte[(int)(seconds * WindowsMicrophone.SampleRate) * 2];
            for (var i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), value);
            _onData!(bytes);
        }

        public void Unplug() => _onFailure!(new MicrophoneException("The microphone is being used by another program. Close that program, or choose another microphone."));

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ARecordingIsSavedAndHandedOnAsTheFileToUse()
    {
        var root = NewRoot();
        try
        {
            var room = new Room();
            using var recorder = new RecorderViewModel(room, root, action => action());
            Assert.Equal(["Default microphone", "Desk microphone"], recorder.Devices.Select(device => device.Label));
            Assert.Equal(WindowsMicrophone.DefaultDevice, recorder.SelectedDevice!.Id);
            Assert.True(recorder.CanRecord);
            Assert.True(recorder.CanChooseDevice);

            RecordedFile? handedOn = null;
            recorder.Recorded += file => handedOn = file;
            recorder.ToggleRecordingCommand.Execute(null);
            Assert.True(recorder.IsRecording);
            Assert.False(recorder.CanChooseDevice);                      // not while recording
            Assert.Equal("Recording…", recorder.Status);
            room.Say(65, 4000);                                          // a minute and five seconds
            recorder.Tick();
            Assert.Equal("1:05", recorder.Elapsed);
            Assert.Equal(4000 / 32767.0 * 100, recorder.Level, 2);

            recorder.ToggleRecordingCommand.Execute(null);
            Assert.False(recorder.IsRecording);
            Assert.NotNull(handedOn);
            Assert.StartsWith(root, handedOn!.Path);
            Assert.True(File.Exists(handedOn.Path));
            Assert.Equal("1:05", recorder.Elapsed);
            Assert.Equal($"Recording saved: {Path.GetFileName(handedOn.Path)} (1:05)", recorder.Status);
            Assert.Equal(0, recorder.Level);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void ANamedDeviceCanBeChosen()
    {
        var root = NewRoot();
        try
        {
            var room = new Room();
            using var recorder = new RecorderViewModel(room, root, action => action());
            recorder.SelectedDevice = recorder.Devices[1];
            recorder.ToggleRecordingCommand.Execute(null);
            Assert.Equal(0, room.Opened);
            recorder.ToggleRecordingCommand.Execute(null);        // nothing was said
            Assert.Empty(Directory.GetFiles(root));               // ...so nothing is kept
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void ARecordingOfNothingIsNotKeptAndTheStatusSaysWhatToCheck()
    {
        var root = NewRoot();
        try
        {
            var room = new Room();
            using var recorder = new RecorderViewModel(room, root, action => action());
            var handedOn = false;
            recorder.Recorded += _ => handedOn = true;
            recorder.ToggleRecordingCommand.Execute(null);
            room.Say(2, 0);
            recorder.Tick();
            Assert.StartsWith("No sound is coming from the microphone.", recorder.Status);   // said while it is still recording
            recorder.ToggleRecordingCommand.Execute(null);
            Assert.StartsWith("No sound was recorded.", recorder.Status);
            Assert.Contains("Privacy & security", recorder.Status);
            Assert.False(handedOn);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void WithoutAMicrophoneTheCardSaysSoAndCannotRecord()
    {
        var room = new Room();
        room.Present.RemoveAt(1);
        using var recorder = new RecorderViewModel(room, NewRoot(), action => action());
        Assert.Empty(recorder.Devices);
        Assert.False(recorder.CanRecord);
        Assert.StartsWith("No microphone was found.", recorder.Status);
        recorder.ToggleRecordingCommand.Execute(null);
        Assert.False(recorder.IsRecording);

        room.Present.Add(new(0, "New microphone"));               // plugged in afterwards
        recorder.RefreshDevices();
        Assert.True(recorder.CanRecord);
        Assert.Equal("", recorder.Status);
    }

    [Fact]
    public void AMicrophoneThatCannotBeOpenedIsExplainedInTheInterfaceLanguage()
    {
        var before = Loc.Instance.Language;
        var root = NewRoot();
        try
        {
            var room = new Room { CannotOpen = new MicrophoneException("The microphone is being used by another program. Close that program, or choose another microphone.") };
            using var recorder = new RecorderViewModel(room, root, action => action());
            recorder.ToggleRecordingCommand.Execute(null);
            Assert.False(recorder.IsRecording);
            Assert.StartsWith("The microphone is being used by another program.", recorder.Status);
            Loc.Instance.SetLanguage("de");
            recorder.RefreshTexts();
            Assert.StartsWith("Das Mikrofon wird von einem anderen Programm verwendet.", recorder.Status);
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public void WhenTheMicrophoneGoesAwayWhatWasRecordedIsKept()
    {
        var root = NewRoot();
        try
        {
            var room = new Room();
            using var recorder = new RecorderViewModel(room, root, action => action());
            RecordedFile? handedOn = null;
            recorder.Recorded += file => handedOn = file;
            recorder.ToggleRecordingCommand.Execute(null);
            room.Say(3, 900);
            room.Unplug();
            Assert.False(recorder.IsRecording);
            Assert.NotNull(handedOn);
            Assert.Equal(3.0, handedOn!.Duration.TotalSeconds, 2);
            Assert.StartsWith("The recording stopped: ", recorder.Status);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void ClosingTheWindowSavesARecordingThatIsRunning()
    {
        var root = NewRoot();
        try
        {
            var room = new Room();
            using var recorder = new RecorderViewModel(room, root, action => action());
            RecordedFile? handedOn = null;
            recorder.Recorded += file => handedOn = file;
            recorder.ToggleRecordingCommand.Execute(null);
            room.Say(2, 700);
            recorder.StopForExit();
            Assert.NotNull(handedOn);
            Assert.True(File.Exists(handedOn!.Path));
            recorder.StopForExit();                                  // nothing running: nothing happens
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheWordsFollowTheInterfaceLanguage()
    {
        var before = Loc.Instance.Language;
        try
        {
            using var recorder = new RecorderViewModel(new Room(), NewRoot(), action => action());
            Loc.Instance.SetLanguage("hu");
            recorder.RefreshTexts();
            Assert.Equal("Alapértelmezett mikrofon", recorder.Devices[0].Label);
            Assert.Equal("Desk microphone", recorder.Devices[1].Label);       // the name the device gives itself is a fact
        }
        finally { Loc.Instance.SetLanguage(before); }
    }

    [Fact]
    public async Task StudioChoosesTheFinishedRecordingAsTheFileToTranscribeAndDoesNotUpdateWhileItRecords()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            var room = new Room();
            shell.Microphone = room;
            await shell.InitializeAsync();
            Assert.False(shell.IsRecordingNow);
            shell.Recorder.ToggleRecordingCommand.Execute(null);
            Assert.True(shell.IsRecordingNow);
            room.Say(2, 1200);
            shell.Recorder.ToggleRecordingCommand.Execute(null);
            Assert.False(shell.IsRecordingNow);
            Assert.StartsWith(Path.Combine(root, "Recordings"), shell.SourcePath);
            Assert.True(File.Exists(shell.SourcePath));
            Assert.StartsWith("Recording saved: ", shell.Status);
            await shell.Host.DisposeAsync();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheClientsSendPageChoosesTheFinishedRecordingToo()
    {
        var root = NewRoot();
        try
        {
            using var workspace = new RemoteWorkspaceViewModel(action => action(), (_, _) => { }, Path.Combine(root, "Temp"), Path.Combine(root, "Recordings"));
            var room = new Room();
            workspace.Microphone = room;
            workspace.Recorder.ToggleRecordingCommand.Execute(null);
            room.Say(2, 1500);
            workspace.Recorder.ToggleRecordingCommand.Execute(null);
            Assert.StartsWith(Path.Combine(root, "Recordings"), workspace.SourcePath);
            Assert.True(File.Exists(workspace.SourcePath));
        }
        finally { TestCleanup.Delete(root); }
    }
}
