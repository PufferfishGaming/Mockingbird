using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TriAsr.Application;

namespace TriAsr.Hardware;

public sealed record GpuInfo(string Name, uint VendorId, ulong DedicatedBytes, string DriverVersion);
public sealed record CpuTopology(int PhysicalCores, int LogicalProcessors, int? PerformanceCores, int? EfficiencyCores);
public sealed record HardwareProfile(string Cpu, string CpuVendor, CpuTopology Topology, ulong RamBytes,
    bool Avx, bool Avx2, bool Fma, bool F16c, IReadOnlyList<GpuInfo> Gpus, IReadOnlyList<string> VulkanDevices,
    string RocmStatus, IReadOnlyList<string> NpuDevices, string WindowsVersion, long FreeDiskBytes, string Fingerprint);

[SupportedOSPlatform("windows")]
public sealed class HardwareProfiler(IProcessRunner runner)
{
    public async Task<HardwareProfile> DetectAsync(string dataRoot, string whisperExecutable, CancellationToken token = default)
    {
        var cpu = "Unknown CPU";
        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            cpu = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? cpu;
        var topology = ReadTopology();
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new System.ComponentModel.Win32Exception();
        const string query = "$ErrorActionPreference='Stop'; @{gpu=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion); npu=@(Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match 'Neural|\\bNPU\\b|AI Boost' } | Select-Object -ExpandProperty Name)} | ConvertTo-Json -Depth 5 -Compress";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(query));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var probe = await runner.RunAsync(new(powershell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], dataRoot, TimeSpan.FromSeconds(30)), token);
        var drivers = new Dictionary<string, string>();
        var npu = new List<string>();
        if (probe.ExitCode == 0)
        {
            using var document = JsonDocument.Parse(probe.StandardOutput);
            foreach (var gpu in document.RootElement.GetProperty("gpu").EnumerateArray())
                drivers[gpu.GetProperty("Name").GetString() ?? ""] = gpu.GetProperty("DriverVersion").GetString() ?? "Unknown";
            foreach (var device in document.RootElement.GetProperty("npu").EnumerateArray()) npu.Add(device.GetString() ?? "Unknown");
        }
        else npu.Add("Probe unavailable");
        var gpus = ReadGpus().Select(gpu => gpu with { DriverVersion = drivers.GetValueOrDefault(gpu.Name, "Unknown") }).ToArray();
        var vulkan = new List<string>();
        if (File.Exists(whisperExecutable))
        {
            var result = await runner.RunAsync(new(whisperExecutable, ["--help"], Path.GetDirectoryName(whisperExecutable)!, TimeSpan.FromSeconds(30)), token);
            foreach (var line in result.StandardError.Split('\n'))
                if (line.StartsWith("ggml_vulkan:") && line.Contains(" = ")) vulkan.Add(line.Trim());
        }
        using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = os?.GetValue("ProductName")?.ToString() ?? "Windows";
        if (int.TryParse(os?.GetValue("CurrentBuild")?.ToString(), out var build) && build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        var version = $"{product} build {build}.{os?.GetValue("UBR")}";
        var fingerprintInput = JsonSerializer.Serialize(new { cpu, memory.TotalPhysical, gpus, vulkan, Rocm = "Unvalidated", AppVersion = typeof(HardwareProfiler).Assembly.GetName().Version?.ToString() });
        return new(cpu, ReadCpuVendor(), topology, memory.TotalPhysical, Avx.IsSupported, Avx2.IsSupported, Fma.IsSupported,
            X86Base.IsSupported && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0, gpus, vulkan,
            "Not validated by an installed inference runtime", npu, version,
            new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataRoot))!).AvailableFreeSpace,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))));
    }
    private static string ReadCpuVendor()
    {
        if (!X86Base.IsSupported) return "Unknown";
        var registers = X86Base.CpuId(0, 0);
        return Encoding.ASCII.GetString(BitConverter.GetBytes(registers.Ebx).Concat(BitConverter.GetBytes(registers.Edx)).Concat(BitConverter.GetBytes(registers.Ecx)).ToArray());
    }
    private static CpuTopology ReadTopology()
    {
        uint size = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size)) return new(Environment.ProcessorCount, Environment.ProcessorCount, null, null);
            var classes = new List<byte>();
            for (var offset = 0; offset < size;)
            {
                var item = IntPtr.Add(buffer, offset);
                var itemSize = Marshal.ReadInt32(item, 4);
                if (itemSize < 10) throw new InvalidDataException("Invalid CPU topology record.");
                classes.Add(Marshal.ReadByte(item, 9));
                offset += itemSize;
            }
            var hybrid = classes.Distinct().Count() > 1;
            var performance = hybrid ? classes.Count(value => value == classes.Max()) : (int?)null;
            return new(classes.Count, Environment.ProcessorCount, performance, hybrid ? classes.Count - performance : null);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        var result = CreateDXGIFactory1(ref iid, out var factory);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            var enumerate = Vtable<EnumAdapters>(factory, 12);
            var adapters = new List<GpuInfo>();
            for (uint index = 0; enumerate(factory, index, out var adapter) == 0; index++)
            {
                try
                {
                    Marshal.ThrowExceptionForHR(Vtable<GetDescription>(adapter, 10)(adapter, out var description));
                    if ((description.Flags & 2) == 0) adapters.Add(new(description.Description.TrimEnd('\0'), description.VendorId, description.DedicatedVideoMemory, "Unknown"));
                }
                finally { Marshal.Release(adapter); }
            }
            return adapters;
        }
        finally { Marshal.Release(factory); }
    }
    private static T Vtable<T>(IntPtr instance, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(IntPtr instance, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr instance, out AdapterDescription description);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public ulong DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr information, ref uint length);
}

public static class GpuMemoryPlanner
{
    public static ulong SafeBudget(ulong dedicatedBytes) => (ulong)(dedicatedBytes * 0.8);
    public static bool Fits(ulong dedicatedBytes, ulong weights, ulong runtimeBuffers, ulong kvCache, ulong temporaryBuffers)
    {
        try { return checked(weights + runtimeBuffers + kvCache + temporaryBuffers) <= SafeBudget(dedicatedBytes); }
        catch (OverflowException) { return false; }
    }
}
