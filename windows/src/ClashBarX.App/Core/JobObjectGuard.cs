using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClashBarX.App.Core;

/// <summary>
/// Windows Job Object 守护：子进程挂入 KILL_ON_JOB_CLOSE 的 Job，
/// 即使宿主进程崩溃（来不及走正常退出路径），内核子进程也会随 Job 关闭被系统终结，
/// 不会成为继续劫持流量的孤儿（App 层允许 P/Invoke；Core 层红线不受影响）。
/// </summary>
internal sealed partial class JobObjectGuard : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _job;

    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>把进程挂入 Job（惰性创建 Job）。失败返回 false，由调用方记录日志。</summary>
    public bool TryAssign(Process process)
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            if (_job == IntPtr.Zero && !TryCreateJob(out _job))
            {
                return false;
            }

            return AssignProcessToJobObject(_job, process.SafeHandle.DangerousGetHandle());
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>关闭 Job 句柄：Kill-on-close 生效，Job 内全部进程（含孙进程）被终结。</summary>
    public void Dispose()
    {
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    private bool TryCreateJob(out IntPtr job)
    {
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return false;
        }

        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                ref info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(job);
            job = IntPtr.Zero;
            return false;
        }

        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, int cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
