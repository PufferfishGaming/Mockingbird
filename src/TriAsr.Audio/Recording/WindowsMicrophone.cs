using System.Runtime.InteropServices;

namespace TriAsr.Audio.Recording;

/// <summary>
/// Records with the Windows wave-in API (winmm), which every Windows version has: no package, no extra program, and the same in every edition.
/// The sound is asked for as 16 kHz, mono, 16-bit, which is what the speech programs want, so a recording is small (about 2 MB a minute) and
/// needs no conversion; Windows converts from the device's own format.
/// </summary>
public sealed class WindowsMicrophone : IMicrophone
{
    public const int DefaultDevice = -1;
    public const int SampleRate = 16000;
    private const int BufferCount = 4;
    private const int BufferBytes = SampleRate / 10 * 2; // a tenth of a second

    public IReadOnlyList<InputDevice> Devices()
    {
        var devices = new List<InputDevice> { new(DefaultDevice, "") };
        try
        {
            var count = Native.waveInGetNumDevs();
            for (var index = 0; index < count; index++)
            {
                var capabilities = new Native.WaveInCapabilities();
                if (Native.waveInGetDevCaps((UIntPtr)(uint)index, ref capabilities, Marshal.SizeOf<Native.WaveInCapabilities>()) == 0)
                    devices.Add(new InputDevice(index, capabilities.Name));
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return []; } // not Windows
        return devices.Count == 1 ? [] : devices;   // only the default entry means there is no device at all
    }

    public IDisposable Start(int deviceId, Action<ReadOnlyMemory<byte>> onData, Action<Exception> onFailure)
    {
        var format = new Native.WaveFormat
        {
            FormatTag = 1, Channels = 1, SamplesPerSecond = SampleRate, BitsPerSample = 16, BlockAlign = 2, AverageBytesPerSecond = SampleRate * 2, ExtraSize = 0
        };
        var arrived = new AutoResetEvent(false);
        var result = Native.waveInOpen(out var handle, deviceId == DefaultDevice ? Native.WaveMapper : (uint)deviceId, ref format, arrived.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, Native.CallbackEvent);
        if (result != 0) { arrived.Dispose(); throw new MicrophoneException(Explain(result)); }
        return new Capture(handle, arrived, onData, onFailure);
    }

    /// <summary>What an error code of the wave-in API means for the person at the keyboard.</summary>
    public static string Explain(int result) => result switch
    {
        2 or 6 => "No microphone was found. Connect one, or check that it is enabled in the Windows sound settings.",
        4 => "The microphone is being used by another program. Close that program, or choose another microphone.",
        32 => "This microphone cannot record in the format the speech programs need.",
        _ => $"The microphone could not be opened (Windows error {result}). Check that it is connected and not in use by another program."
    };

    private sealed class Capture : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly AutoResetEvent _arrived;
        private readonly Action<ReadOnlyMemory<byte>> _onData;
        private readonly Action<Exception> _onFailure;
        private readonly IntPtr[] _headers = new IntPtr[BufferCount];
        private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
        private readonly Thread _worker;
        private volatile bool _stopping;
        private bool _disposed;
        private static readonly int HeaderSize = Marshal.SizeOf<Native.WaveHeader>();

        public Capture(IntPtr handle, AutoResetEvent arrived, Action<ReadOnlyMemory<byte>> onData, Action<Exception> onFailure)
        {
            _handle = handle; _arrived = arrived; _onData = onData; _onFailure = onFailure;
            try
            {
                for (var index = 0; index < BufferCount; index++)
                {
                    _buffers[index] = Marshal.AllocHGlobal(BufferBytes);
                    _headers[index] = Marshal.AllocHGlobal(HeaderSize);
                    Queue(index);
                }
                Check(Native.waveInStart(_handle));
            }
            catch { Release(); throw; }
            _worker = new Thread(Work) { IsBackground = true, Name = "Microphone", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }

        private static void Check(int result) { if (result != 0) throw new MicrophoneException(Explain(result)); }

        private void Queue(int index)
        {
            Marshal.StructureToPtr(new Native.WaveHeader { Data = _buffers[index], BufferLength = BufferBytes }, _headers[index], false);
            Check(Native.waveInPrepareHeader(_handle, _headers[index], HeaderSize));
            Check(Native.waveInAddBuffer(_handle, _headers[index], HeaderSize));
        }

        private void Work()
        {
            try
            {
                while (!_stopping)
                {
                    _arrived.WaitOne(250);
                    Drain();
                }
            }
            catch (Exception error) when (!_stopping) { _onFailure(error); }
        }

        /// <summary>Hands on every buffer Windows has filled and puts it back in the queue.</summary>
        private void Drain()
        {
            for (var index = 0; index < BufferCount; index++)
            {
                var header = Marshal.PtrToStructure<Native.WaveHeader>(_headers[index]);
                if ((header.Flags & Native.Done) == 0) continue;
                if (header.BytesRecorded > 0)
                {
                    var piece = new byte[header.BytesRecorded];
                    Marshal.Copy(header.Data, piece, 0, piece.Length);
                    _onData(piece);
                }
                Native.waveInUnprepareHeader(_handle, _headers[index], HeaderSize);
                if (!_stopping) Queue(index);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            _arrived.Set();
            _worker.Join(2000);
            Native.waveInStop(_handle);
            Native.waveInReset(_handle);          // returns the queued buffers, with what they hold
            try { Drain(); } catch (Exception error) when (error is MicrophoneException or ExternalException) { }
            Release();
        }

        private void Release()
        {
            for (var index = 0; index < BufferCount; index++)
            {
                if (_headers[index] != IntPtr.Zero) { Native.waveInUnprepareHeader(_handle, _headers[index], HeaderSize); Marshal.FreeHGlobal(_headers[index]); _headers[index] = IntPtr.Zero; }
                if (_buffers[index] != IntPtr.Zero) { Marshal.FreeHGlobal(_buffers[index]); _buffers[index] = IntPtr.Zero; }
            }
            Native.waveInClose(_handle);
            _arrived.Dispose();
        }
    }

    private static class Native
    {
        public const uint WaveMapper = 0xFFFFFFFF;
        public const uint CallbackEvent = 0x00050000;
        public const uint Done = 0x1;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WaveFormat
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSecond, AverageBytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength, BytesRecorded;
            public IntPtr User;
            public uint Flags, Loops;
            public IntPtr Next, Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WaveInCapabilities
        {
            public ushort ManufacturerId, ProductId;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
        }

        [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] public static extern int waveInGetDevCaps(UIntPtr deviceId, ref WaveInCapabilities capabilities, int size);
        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int waveInStop(IntPtr handle);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr handle);
    }
}
