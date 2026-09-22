using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Blazorly.Harness.Core.Jobs;

/// <summary>Keeps Windows descendants owned by their run, even after an intermediate shell exits.</summary>
public sealed class WindowsProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();

    private WindowsProcessJob(SafeFileHandle handle) => _handle = handle;

    public static WindowsProcessJob? Create()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
        if (!SetInformationJobObject(handle, 9 /* ExtendedLimitInformation */, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return new(handle);
    }

    public void Assign(Process process)
    {
        lock (_gate)
        {
            if (AssignProcessToJobObject(_handle, process.SafeHandle)) return;
            var error = Marshal.GetLastWin32Error();
            // Very short commands can exit between Process.Start and assignment.
            if (process.HasExited) return;
            throw new Win32Exception(error, "Could not track this command's child processes.");
        }
    }

    public void Terminate()
    {
        lock (_gate)
            if (!_handle.IsClosed && !TerminateJobObject(_handle, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public async Task WaitForEmptyAsync(CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_handle.IsClosed) return;
                if (!QueryInformationJobObject(_handle, 1 /* BasicAccountingInformation */, out var accounting,
                        (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (accounting.ActiveProcesses == 0) return;
            }
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("The command's child processes have not stopped yet.");
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_gate) _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out BasicAccounting info, uint length, IntPtr returnedLength);
}
