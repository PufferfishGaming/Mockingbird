using System.Runtime.InteropServices;

namespace TriAsr.Hardware;

/// <summary>Where the computer's power comes from. Desktops without a battery report "ac" or "unknown", never "battery".</summary>
public static class PowerSource
{
    public const string Ac = "ac", Battery = "battery", Unknown = "unknown";

    public static string Read() => OperatingSystem.IsWindows() && GetSystemPowerStatus(out var status) ? Map(status.AcLineStatus) : Unknown;

    /// <summary>Maps the Windows ACLineStatus byte: 0 battery, 1 AC, 255 or anything else unknown.</summary>
    public static string Map(byte acLineStatus) => acLineStatus switch { 0 => Battery, 1 => Ac, _ => Unknown };

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
