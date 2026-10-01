using System.IO;

namespace TriAsr.App;

public sealed class RuntimePaths
{
    public string Root { get; } = DiscoverRoot();
    private readonly StorageLocations.Location _location = StorageLocations.Load();
    public string? StorageLoadError { get; } = StorageLocations.LastLoadError;
    public string DefaultDataRoot => _location.DataRoot ?? (File.Exists(Path.Combine(Root, "TriAsr.slnx")) ? Root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TriASR"));
    public string ModelRoot => Environment.GetEnvironmentVariable("TRIASR_MODEL_ROOT") is { Length: > 0 } configured ? Path.GetFullPath(configured) : _location.ModelRoot ?? DefaultDataRoot;
    public string Ffmpeg => Path.Combine(Root, "Runtimes", "FFmpeg", "ffmpeg.exe");
    public string Whisper => Path.Combine(Root, "Runtimes", "Whisper-Vulkan", "whisper-cli.exe");
    private string? _whisperModel;
    public string WhisperModel { get => _whisperModel ?? Path.Combine(ModelRoot, "models", "ggml-large-v3.bin"); set => _whisperModel = value; }
    public string CanaryRuntime => Path.Combine(Root, "Runtimes", "Canary", "transcribe-native-windows-x86_64-cpu-vulkan");
    private string? _canaryModel;
    public string CanaryModel { get => _canaryModel ?? Path.Combine(ModelRoot, "Models", "Canary", "canary-1b-v2-Q8_0.gguf"); set => _canaryModel = value; }
    public string CanaryWorker => Path.Combine(AppContext.BaseDirectory, "Workers", "Canary", "TriAsr.Worker.exe");
    public string LlamaServer => Path.Combine(Root, "Runtimes", "Llama", "llama-server.exe");
    public string WhisperFor(string backend) => backend is "cpu" or "vulkan" ? Whisper : Path.Combine(BackendRuntimes.Folder(this, "Whisper", backend), "whisper-cli.exe");
    public string CanaryFor(string backend) => backend is "cpu" or "vulkan" ? CanaryRuntime : BackendRuntimes.Folder(this, "Canary", backend);
    public string CorrectionFor(string backend) => backend is "cpu" or "vulkan" ? LlamaServer : Path.Combine(BackendRuntimes.Folder(this, "Correction", backend), "llama-server.exe");
    private string? _correctionModel;
    public string CorrectionModel { get => _correctionModel ?? Path.Combine(ModelRoot, "Models", "Correction", "Qwen3-4B-Instruct-2507-Q6_K.gguf"); set => _correctionModel = value; }
    public string ConfigurationFingerprint(string hardwareFingerprint)
    {
        var runtimeDirectory = Path.Combine(Root, "Runtimes");
        var dependencies = Directory.Exists(runtimeDirectory)
            ? Directory.EnumerateFiles(runtimeDirectory, "*", SearchOption.AllDirectories).Where(path => Path.GetExtension(path) is ".dll" or ".exe")
            : Enumerable.Empty<string>();
        var optionalDirectory = Path.Combine(ModelRoot, "BackendRuntimes");
        if (Directory.Exists(optionalDirectory)) dependencies = dependencies.Concat(Directory.EnumerateFiles(optionalDirectory, "*", SearchOption.AllDirectories).Where(path => Path.GetExtension(path) is ".dll" or ".exe"));
        var signature = string.Join("|", new[] { Whisper, WhisperModel, CanaryWorker, Path.Combine(CanaryRuntime, "transcribe.dll"), CanaryModel, LlamaServer, CorrectionModel }
            .Concat(dependencies).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => { var info = new FileInfo(path); return info.Exists ? $"{path}:{info.Length}:{info.LastWriteTimeUtc.Ticks}" : $"{path}:missing"; }));
        var algorithms = string.Join("|", new[] { typeof(RuntimePaths).Assembly.ManifestModule.ModuleVersionId,
            typeof(TriAsr.Alignment.TokenAligner).Assembly.ManifestModule.ModuleVersionId,
            typeof(TriAsr.Fusion.DisagreementDetector).Assembly.ManifestModule.ModuleVersionId,
            typeof(TriAsr.Domain.FinalTranscript).Assembly.ManifestModule.ModuleVersionId });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature + hardwareFingerprint + algorithms)));
    }
    private static string DiscoverRoot()
    {
        if (Environment.GetEnvironmentVariable("TRIASR_RUNTIME_ROOT") is { Length: > 0 } configured) return Path.GetFullPath(configured);
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Runtimes", "FFmpeg", "ffmpeg.exe"))) return AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TriAsr.slnx"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
}
