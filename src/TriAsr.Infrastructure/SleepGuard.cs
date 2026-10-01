using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TriAsr.Infrastructure;

/// <summary>
/// Asks Windows not to put the computer to sleep while a long job runs. The display may still turn off, and the request
/// disappears when disposed or when the process exits. It is a process-wide power request, not tied to one thread, so it
/// is safe across <c>await</c>. Failure to create the request is harmless: the job simply runs without protection.
/// </summary>
public sealed class SleepGuard : IDisposable
{
    private SafeFileHandle? _request;
    private SleepGuard(SafeFileHandle request) => _request = request;

    public static IDisposable Begin(string reason)
    {
        if (!OperatingSystem.IsWindows()) return NoGuard.Instance;
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = text };
            var request = PowerCreateRequest(ref context);
            if (request.IsInvalid) return NoGuard.Instance;
            if (!PowerSetRequest(request, 1)) { request.Dispose(); return NoGuard.Instance; }
            return new SleepGuard(request);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or ArgumentException) { return NoGuard.Instance; }
        finally { Marshal.FreeHGlobal(text); }
    }

    public void Dispose()
    {
        var request = Interlocked.Exchange(ref _request, null);
        if (request is null) return;
        try { PowerClearRequest(request, 1); } finally { request.Dispose(); }
    }

    private sealed class NoGuard : IDisposable
    {
        public static readonly NoGuard Instance = new();
        public void Dispose() { }
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ReasonContext
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public IntPtr SimpleReasonString;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerSetRequest(SafeFileHandle request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerClearRequest(SafeFileHandle request, int type);
}
