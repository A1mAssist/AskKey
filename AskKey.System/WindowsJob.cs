using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AskKey.System;

/// <summary>
/// Windows counterpart of the Swift posix_spawn + private process group topology.
/// Every child runs inside a Job Object, so killing the job terminates descendants
/// that would otherwise survive a direct TerminateProcess.
/// </summary>
internal static class WindowsJob
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, uint infoClass, ref JobObjectExtendedLimitInformationStruct info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        var info = new JobObjectExtendedLimitInformationStruct();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>()))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject");
            CloseHandle(job);
            throw error;
        }
        return job;
    }

    public static void Assign(IntPtr job, Process process)
    {
        if (AssignProcessToJobObject(job, process.Handle)) return;
        var error = Marshal.GetLastWin32Error();
        // The child may already sit in a job that forbids breakaway (common under CI hosts).
        // Killing it here keeps the guarantee that no untracked child survives.
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        throw new Win32Exception(error, "AssignProcessToJobObject");
    }

    public static void Terminate(IntPtr job, uint exitCode) => TerminateJobObject(job, exitCode);

    public static void Close(IntPtr job)
    {
        if (job != IntPtr.Zero) CloseHandle(job);
    }
}
