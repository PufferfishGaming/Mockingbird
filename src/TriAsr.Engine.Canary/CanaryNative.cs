using System.Diagnostics;
using System.Runtime.InteropServices;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Engine.Canary;

/// <summary>Only call inside the isolated worker. ABI pinned to upstream v0.2.4.</summary>
public static class CanaryNative
{
    public sealed record Result(EngineTranscript Transcript, IReadOnlyList<string> RawChunks, string NativeBackend);
    /// <summary>Reads the whole recording in the windows the caller supplies (the original behaviour, used when there is no chunk plan).</summary>
    public static Result Transcribe(string runtime, string modelPath, string language, string backend, int threads,
        IEnumerable<float[]> chunks, double audioSeconds, Action<double>? progress = null) =>
        Run(runtime, modelPath, language, backend, threads, audioSeconds, recognize =>
        {
            var texts = new List<string>(); var raw = new List<string>();
            long processedSamples = 0;
            foreach (var samples in chunks)
            {
                var (full, rawText) = recognize(samples);
                texts.Add(full); raw.Add(rawText);
                processedSamples += samples.Length;
                progress?.Invoke(Math.Clamp(processedSamples / (audioSeconds * 16000), 0, 1));
            }
            return (string.Join(" ", texts), raw, []);
        });

    /// <summary>Reads exactly the planned windows. Every finished window is saved in <paramref name="checkpointDirectory"/> so a restarted run continues.</summary>
    public static Result TranscribeWindows(string runtime, string modelPath, string language, string backend, int threads,
        IReadOnlyList<CanaryWindow> windows, string? checkpointDirectory, Func<CanaryWindow, IEnumerable<float[]>> readPieces,
        double audioSeconds, Action<double>? progress = null) =>
        Run(runtime, modelPath, language, backend, threads, audioSeconds, recognize =>
        {
            var results = CanaryWindowRunner.Run(windows, checkpointDirectory, $"{Path.GetFileName(modelPath)}|{language}", readPieces, recognize, progress);
            var (text, segments) = CanaryWindowRunner.Assemble(results);
            return (text, results.Select(result => result.Raw).ToList(), segments);
        });

    private static Result Run(string runtime, string modelPath, string language, string backend, int threads, double audioSeconds,
        Func<Func<float[], (string Full, string Raw)>, (string Text, List<string> Raw, IReadOnlyList<TranscriptSegment> Segments)> process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!SetDllDirectory(runtime)) throw new System.ComponentModel.Win32Exception();
        NativeLibrary.SetDllImportResolver(typeof(CanaryNative).Assembly, (name, assembly, search) =>
            name == "transcribe" ? NativeLibrary.Load(Path.Combine(runtime, "transcribe.dll")) : IntPtr.Zero);
        var version = String(Native.transcribe_version());
        if (version != "0.2.4") throw new InvalidOperationException($"Canary ABI version mismatch: {version}");
        Validate<LoadParams>(0); Validate<SessionParams>(1); Validate<RunParams>(2); Validate<DeviceInfo>(13);
        Check(Native.transcribe_init_backends(runtime));
        Native.transcribe_model_load_params_init(out var load);
        load.Backend = backend switch { "cpu" => 1, "vulkan" => 3, "cuda" => 5, "rocm" => 6, _ => throw new ArgumentException("Unsupported backend.") };
        Native.transcribe_session_params_init(out var sessionParams);
        sessionParams.Threads = threads;
        var stopwatch = Stopwatch.StartNew();
        Check(Native.transcribe_open(modelPath, ref load, ref sessionParams, out var session));
        var loadSeconds = stopwatch.Elapsed.TotalSeconds;
        try
        {
            var model = Native.transcribe_get_model(session);
            var nativeBackend = String(Native.transcribe_model_backend(model));
            Native.transcribe_device_info_init(out var device);
            Check(Native.transcribe_device_get_info(Native.transcribe_model_device(model), ref device));
            var actual = String(device.Kind).ToLowerInvariant();
            if (actual == "hip") actual = "rocm";
            if (!actual.Equals(backend, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Requested {backend}, actual backend is {actual} ({nativeBackend}).");
            var languagePointer = Marshal.StringToCoTaskMemUTF8(language);
            try
            {
                (string Full, string Raw) Recognize(float[] samples)
                {
                    Native.transcribe_run_params_init(out var run);
                    run.Language = languagePointer;
                    run.Timestamps = 0; // Canary v2 port exposes no speech timestamps.
                    run.SpeculativeDrafts = 0;
                    Check(Native.transcribe_run(session, samples, samples.Length, ref run));
                    return (String(Native.transcribe_full_text(session)), String(Native.transcribe_raw_text(session)));
                }
                var (text, raw, segments) = process(Recognize);
                return new(new("Canary", Path.GetFileName(modelPath), version, backend, actual, String(device.Description),
                    language, audioSeconds, stopwatch.Elapsed.TotalSeconds, segments, text, false, loadSeconds,
                    Process.GetCurrentProcess().PeakWorkingSet64), raw, nativeBackend);
            }
            finally { Marshal.FreeCoTaskMem(languagePointer); }
        }
        finally { Native.transcribe_session_free(session); }
    }
    private static string String(IntPtr value) => Marshal.PtrToStringUTF8(value) ?? "";
    private static void Check(int status) { if (status != 0) throw new InvalidOperationException($"Canary status {status}: {String(Native.transcribe_status_string(status))}"); }
    private static void Validate<T>(int which) where T : struct
    {
        var actual = Native.transcribe_abi_struct_size(which);
        if (actual != (nuint)Marshal.SizeOf<T>()) throw new InvalidOperationException($"Canary ABI struct {which}: managed={Marshal.SizeOf<T>()}, native={actual}");
    }
    [StructLayout(LayoutKind.Sequential)] private struct LoadParams { public ulong Size; public int Backend; public IntPtr Device; }
    [StructLayout(LayoutKind.Sequential)] private struct SessionParams { public ulong Size; public int Threads, KvType, Context; }
    [StructLayout(LayoutKind.Sequential)] private struct RunParams
    {
        public ulong Size; public int Task, Timestamps, Pnc, Itn, Diarize;
        public IntPtr Language, TargetLanguage;
        [MarshalAs(UnmanagedType.I1)] public bool KeepSpecial;
        public IntPtr Family;
        public int SpeculativeDrafts;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo
    {
        public ulong Size; public IntPtr Name, Description, Kind, Id; public ulong Total, Free; public int DeviceType;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDllDirectory(string path);
    private static class Native
    {
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr transcribe_version();
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr transcribe_status_string(int status);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern nuint transcribe_abi_struct_size(int which);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern int transcribe_init_backends([MarshalAs(UnmanagedType.LPUTF8Str)] string directory);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern void transcribe_model_load_params_init(out LoadParams parameters);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern void transcribe_session_params_init(out SessionParams parameters);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern void transcribe_run_params_init(out RunParams parameters);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern int transcribe_open([MarshalAs(UnmanagedType.LPUTF8Str)] string model, ref LoadParams load, ref SessionParams parameters, out IntPtr session);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern void transcribe_session_free(IntPtr session);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr transcribe_get_model(IntPtr session);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr transcribe_model_backend(IntPtr model);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr transcribe_model_device(IntPtr model);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern void transcribe_device_info_init(out DeviceInfo info);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern int transcribe_device_get_info(IntPtr device, ref DeviceInfo info);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern int transcribe_run(IntPtr session, [In] float[] samples, int length, ref RunParams parameters);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr transcribe_full_text(IntPtr session);
        [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr transcribe_raw_text(IntPtr session);
    }
}
