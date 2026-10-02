using System.Runtime.InteropServices;
using System.Text;

namespace TriAsr.Infrastructure;

/// <summary>
/// Windows' data protection for the current user: a remembered password is stored so that only this Windows account on this computer can read it
/// back, not as plain text in a file. Where it is not available (another system) nothing is remembered rather than written in the clear.
/// </summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private const int NoUserInterface = 0x1;

    /// <summary>The text, protected and written in base 64; null when it cannot be protected.</summary>
    public static string? Protect(string text) => Run(Encoding.UTF8.GetBytes(text), protect: true) is { } bytes ? Convert.ToBase64String(bytes) : null;

    /// <summary>The text that <see cref="Protect"/> made, or null when it was made for someone else or is damaged.</summary>
    public static string? Unprotect(string protectedText)
    {
        try { return Run(Convert.FromBase64String(protectedText), protect: false) is { } bytes ? Encoding.UTF8.GetString(bytes) : null; }
        catch (FormatException) { return null; }
    }

    private static byte[]? Run(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var blob = new DataBlob { Size = input.Length, Data = pinned.AddrOfPinnedObject() };
            var worked = protect
                ? CryptProtectData(ref blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoUserInterface, out var protectedBlob)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoUserInterface, out protectedBlob);
            if (!worked) return null;
            try
            {
                var output = new byte[protectedBlob.Size];
                Marshal.Copy(protectedBlob.Data, output, 0, output.Length);
                return output;
            }
            finally { LocalFree(protectedBlob.Data); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { pinned.Free(); }
    }
}
