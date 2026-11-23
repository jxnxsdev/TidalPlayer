using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TidalMpvPlayer;

public sealed class MpvProcessJob : IDisposable
{
    private const uint KillProcessesOnJobClose = 0x00002000;
    private readonly SafeFileHandle? handle;
    public bool IsSupported => OperatingSystem.IsWindows();

    public MpvProcessJob()
    {
        if (!IsSupported)
            return;

        var jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (jobHandle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the MPV process job.");

        var limits = new ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = KillProcessesOnJobClose;
        if (!SetInformationJobObject(
                jobHandle,
                JobObjectInfoClass.ExtendedLimitInformation,
                ref limits,
                (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            jobHandle.Dispose();
            throw new Win32Exception(error, "Could not configure MPV process cleanup.");
        }
        handle = jobHandle;
    }

    public bool TryAssign(Process process, out Win32Exception? exception)
    {
        if (handle is null)
        {
            exception = null;
            return false;
        }

        if (AssignProcessToJobObject(handle, process.Handle))
        {
            exception = null;
            return true;
        }

        exception = new Win32Exception(Marshal.GetLastWin32Error());
        return false;
    }

    public void Dispose() => handle?.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInfoClass informationClass,
        ref ExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
