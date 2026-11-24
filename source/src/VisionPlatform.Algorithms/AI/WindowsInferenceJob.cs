using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VisionPlatform.Algorithms.AI;

/// <summary>Windows session boundary: close/terminate kills all descendants, including Python venv launchers.
/// A best-effort Process.Kill tree alone is insufficient when intermediate processes exit.</summary>
internal sealed class WindowsInferenceJob : IDisposable
{
    private readonly SafeJobHandle _handle;
    private WindowsInferenceJob(Process process)
    {
        _handle = CreateJobObjectW(0, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the YOLO process job.");
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(_handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot contain the external YOLO process in its Windows job.");
        }
        catch { _handle.Dispose(); throw; }
    }
    public static WindowsInferenceJob? Attach(Process process) => OperatingSystem.IsWindows() ? new(process) : null;
    // Venv launchers can create the actual interpreter before the parent is assigned.
    // Contain only a verified descendant, never an arbitrary PID reported over a pipe.
    public void ContainWorker(Process launcher, Process worker)
    {
        if (!IsProcessInJob(worker.SafeHandle, _handle, out var contained))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect worker containment.");
        if (contained) return;
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var parents = new Dictionary<uint,uint>(); var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (Process32FirstW(snapshot, ref entry)) do { parents[entry.Id] = entry.ParentId; entry.Size = (uint)Marshal.SizeOf<ProcessEntry>(); } while (Process32NextW(snapshot, ref entry));
        var current = (uint)worker.Id; var seen = new HashSet<uint>();
        while (current != launcher.Id && current != 0 && seen.Add(current) && parents.TryGetValue(current, out var parent)) current = parent;
        if (current != launcher.Id) throw new InvalidDataException("Handshake PID is not this launcher's descendant.");
        if (!AssignProcessToJobObject(_handle, worker.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot contain the actual Python interpreter.");
    }
    public void Terminate() { if (!_handle.IsClosed) TerminateJobObject(_handle, 1); }
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    { protected override bool ReleaseHandle() => CloseHandle(handle); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size, Usage, Id; public nuint Heap; public uint Module, Threads, ParentId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string FileName;
    }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags,uint processId);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(SafeFileHandle snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(SafeFileHandle snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(SafeProcessHandle process,SafeJobHandle job,[MarshalAs(UnmanagedType.Bool)] out bool contained);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
