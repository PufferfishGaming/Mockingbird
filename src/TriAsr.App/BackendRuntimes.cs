using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using TriAsr.Hardware;
using TriAsr.Infrastructure;

namespace TriAsr.App;
public sealed record BackendOption(string Code, string Name)
{
    public override string ToString() => Name;
}
public sealed record BackendAvailability(string Engine, string Backend, string Status, bool CanTest);
public static class BackendRuntimes
{
    public static IReadOnlyList<BackendOption> Options { get; } = [new("cpu", "CPU"), new("vulkan", "Vulkan"), new("cuda", "CUDA (NVIDIA)"), new("rocm", "ROCm / HIP (AMD)")];
    public static string Folder(RuntimePaths paths, string engine, string backend)
    {
        if (engine is not ("Whisper" or "Canary" or "Correction") || backend is not ("cpu" or "vulkan" or "cuda" or "rocm"))
            throw new ArgumentException("Unknown runtime engine or backend.");
        return Path.Combine(paths.ModelRoot, "BackendRuntimes", engine, backend);
    }
    public static string FileName(string engine) => engine switch { "Whisper" => "whisper-cli.exe", "Canary" => "transcribe.dll", "Correction" => "llama-server.exe", _ => throw new ArgumentException("Unknown engine.") };
    public static bool HardwareFits(HardwareProfile profile, string backend) => backend switch
    {
        "cpu" => true, "vulkan" => profile.VulkanDevices.Count > 0,
        "cuda" => profile.Gpus.Any(gpu => gpu.VendorId == 0x10DE), "rocm" => profile.Gpus.Any(gpu => gpu.VendorId == 0x1002), _ => false
    };
    public static bool Installed(RuntimePaths paths, string engine, string backend) => backend is "cpu" or "vulkan"
        ? File.Exists(engine switch { "Whisper" => paths.Whisper, "Canary" => Path.Combine(paths.CanaryRuntime, "transcribe.dll"), _ => paths.LlamaServer })
        : File.Exists(Path.Combine(Folder(paths, engine, backend), FileName(engine)));
    public static string[] Candidates(RuntimePaths paths, HardwareProfile hardware, string engine) => Options.Where(option => HardwareFits(hardware, option.Code) && Installed(paths, engine, option.Code)).Select(option => option.Code).ToArray();
    public static IReadOnlyList<ModelEntry> DownloadAssets(string engine, string backend) => (engine, backend) switch
    {
        ("Whisper", "cuda") => [Asset("whisper-cuda", 674539285, "af520ddd034d985b55dfeea3e465ed93653ba2aee1a55e865033edc548c272a7", "https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-cublas-12.4.0-bin-x64.zip")],
        ("Correction", "cuda") => [Asset("llama-cuda", 263400928, "f4df7ca74cdba0962f762cc4c0e13bd3a4951ad49e043adbb0ede33f8053bb55", "https://github.com/ggml-org/llama.cpp/releases/download/b11321/llama-b11321-bin-win-cuda-12.4-x64.zip"), Asset("llama-cudart", 391443627, "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6", "https://github.com/ggml-org/llama.cpp/releases/download/b11321/cudart-llama-bin-win-cuda-12.4-x64.zip")],
        ("Correction", "rocm") => [Asset("llama-rocm", 256206961, "091556d91f84bc103db088e3600f96a45882b8d4537017d371456cc828237e5d", "https://github.com/ggml-org/llama.cpp/releases/download/b11321/llama-b11321-bin-win-rocm-10.0-x64.zip")],
        ("Canary", "cuda") => [Asset("canary-cuda", 200539913, "d2267b1a703e6db11d81daecd0d82de8cf63a4500f5fd19e2a8ff7de9d0950c1", "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.2.4/transcribe-native-0.2.4-windows-x86_64-cuda.tar.gz")],
        _ => []
    };
    private static ModelEntry Asset(string id, long bytes, string hash, string url) => new(id, "Runtime", id, "Windows x64", "pinned-release", bytes, hash, url, "BackendRuntimes/Downloads/" + id + ".zip", "Upstream runtime licenses");
    public static async Task InstallArchivesAsync(RuntimePaths paths, string engine, string backend, ModelStore store, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var assets = DownloadAssets(engine, backend);
        if (assets.Count == 0) throw new InvalidOperationException(Loc.T("No pinned Windows package is provided for this engine/backend. Import a compatible build instead; Canary requires ABI 0.2.4."));
        var target = Folder(paths, engine, backend);
        var staging = target + ".install-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
        foreach (var asset in assets)
        {
            await store.DownloadAsync(asset, progress, token);
            if (asset.Url.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                await using var compressed = File.OpenRead(store.PathFor(asset));
                await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (await tar.GetNextEntryAsync(false, token) is { } entry)
                {
                    var name = Path.GetFileName(entry.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (Path.GetExtension(name) is not (".dll" or ".exe") || entry.DataStream is null) continue;
                    if (entry.Length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException(Loc.T("Unexpected runtime archive entry size."));
                    var destination = Path.Combine(staging, name);
                    if (File.Exists(destination)) continue;
                    await using var output = File.Create(destination); await entry.DataStream.CopyToAsync(output, token);
                }
                continue;
            }
            using var archive = ZipFile.OpenRead(store.PathFor(asset));
            foreach (var entry in archive.Entries.Where(entry => Path.GetExtension(entry.Name) is ".dll" or ".exe"))
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException(Loc.T("Unexpected runtime archive entry size."));
                var destination = Path.Combine(staging, entry.Name);
                if (File.Exists(destination)) continue;
                await using var input = entry.Open(); await using var output = File.Create(destination);
                await input.CopyToAsync(output, token);
            }
        }
        if (!File.Exists(Path.Combine(staging, FileName(engine)))) throw new InvalidDataException(Loc.T("Archive did not contain the required engine executable."));
        if (Directory.Exists(target)) throw new IOException(Loc.T("A backend is already installed. Existing runtime files are preserved."));
        Directory.Move(staging, target);
    }
    public static async Task ImportAsync(RuntimePaths paths, string engine, string backend, string source, CancellationToken token)
    {
        if (backend is not ("cuda" or "rocm")) throw new InvalidOperationException(Loc.T("CPU and Vulkan are bundled. Import CUDA or ROCm runtime builds only."));
        if (!File.Exists(Path.Combine(source, FileName(engine)))) throw new InvalidDataException(Loc.T("Choose the folder containing {0} and its dependencies.", FileName(engine)));
        var target = Folder(paths, engine, backend);
        if (Directory.Exists(target)) throw new IOException(Loc.T("This backend already has an installed runtime. Existing files are preserved."));
        var staging = target + ".import-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(staging, Path.GetRelativePath(source, file)));
            if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Loc.T("Invalid runtime path."));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(file); await using var output = File.Create(destination); await input.CopyToAsync(output, token);
        }
        Directory.Move(staging, target);
    }
}
