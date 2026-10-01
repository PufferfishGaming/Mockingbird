using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TriAsr.Infrastructure;

// Closing the parent's handle terminates the entire worker job, including on a UI crash.
internal sealed class WorkerOwnership : IDisposable
{
    private readonly SafeFileHandle _job;
    private WorkerOwnership(SafeFileHandle job) => _job = job;
    /// <param name="priority">
    /// When set, every process in the worker's job (including children it starts) runs at this priority class.
    /// The interactive terminal passes nothing, so commands you type yourself keep normal priority.
    /// </param>
    public static WorkerOwnership? TryAttach(Process process, ProcessPriorityClass? priority = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        if (priority is { } requested)
        {
            limits.Basic.LimitFlags |= 0x20; // JOB_OBJECT_LIMIT_PRIORITY_CLASS
            limits.Basic.PriorityClass = requested switch
            {
                ProcessPriorityClass.Idle => 0x40, ProcessPriorityClass.BelowNormal => 0x4000, ProcessPriorityClass.Normal => 0x20,
                ProcessPriorityClass.AboveNormal => 0x8000, ProcessPriorityClass.High => 0x80,
                _ => throw new ArgumentOutOfRangeException(nameof(priority), "Unsupported priority class.")
            };
        }
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            if (process.HasExited) return null;
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new System.ComponentModel.Win32Exception(error, "Could not establish worker lifetime isolation.");
        }
        return new(job);
    }
    public void Dispose() => _job.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTime, PerJobUserTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle handle, int type, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle handle, IntPtr process);
}
