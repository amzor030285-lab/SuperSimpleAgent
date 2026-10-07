using System;
using System.Runtime.InteropServices;

namespace SuperSimpleAgentCLI
{
    /// <summary>
    /// Confines every tool process to a job object so cancel/timeout kills the
    /// whole process tree (cmd, curl, 7za, python, ...) instead of just the tool.
    /// Jobs carry JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so closing the handle also
    /// reaps whatever is still inside, plus a cap on the number of processes.
    /// Limits go through class 9 (JOBOBJECT_EXTENDED_LIMIT_INFORMATION), which is
    /// the only structure that accepts KILL_ON_JOB_CLOSE.
    /// </summary>
    internal static class ProcessJob
    {
        public const int DefaultActiveProcessLimit = 64;

        const uint JobObjectExtendedLimitInformation = 9;
        const uint JobObjectLimitActiveProcess = 0x00000008;
        const uint JobObjectLimitKillOnJobClose = 0x00002000;
        const uint LimitFlags = JobObjectLimitActiveProcess | JobObjectLimitKillOnJobClose;
        const int LimitFlagsOffset = 16;
        const uint ProcessAllAccess = 0x1F0FFF;

        struct Layout
        {
            public int Size;
            public int ActiveProcessLimitOffset;
        }

        // The kernel validates the exact structure length and reads
        // ActiveProcessLimit from a bitness-dependent offset, so the limits are
        // written into a raw buffer instead of a marshalled struct (the CLR
        // struct layout is not what the kernel expects).
        //   64-bit: basic(64) + IO_COUNTERS(48) + 4*SIZE_T(32) = 144, limit @40
        //   32-bit: basic(48) + IO_COUNTERS(48) + 4*SIZE_T(16) = 112, limit @28
        // The 32-bit size is 112 and not 108 because LARGE_INTEGER members force
        // 8-byte alignment of the embedded basic structure.
        // Verified on Windows Server 2008 R2 x64 (144/40) and Windows XP SP3 x86
        // (112/28); the packed 32-bit variant is kept as a probe candidate.
        // The winning layout is resolved once per process against a scratch job
        // and cached; if no candidate is accepted the caller degrades to a plain
        // process kill.
        static readonly Layout[] Layouts = new Layout[]
        {
            new Layout { Size = 144, ActiveProcessLimitOffset = 40 },
            new Layout { Size = 112, ActiveProcessLimitOffset = 28 },
            new Layout { Size = 108, ActiveProcessLimitOffset = 28 }
        };

        static readonly object LayoutLock = new object();
        static bool layoutResolved;
        static int layoutSize;
        static int layoutLimitOffset;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, uint jobInfoClass, IntPtr lpJobInfo, uint jobInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        static bool ApplyLayout(IntPtr job, Layout layout)
        {
            IntPtr buffer = Marshal.AllocHGlobal(layout.Size);
            if (buffer == IntPtr.Zero)
                return false;
            try
            {
                for (int i = 0; i < layout.Size; i++)
                    Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, LimitFlagsOffset, unchecked((int)LimitFlags));
                Marshal.WriteInt32(buffer, layout.ActiveProcessLimitOffset, DefaultActiveProcessLimit);
                return SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)layout.Size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        static bool EnsureLayout()
        {
            lock (LayoutLock)
            {
                if (layoutResolved)
                    return layoutSize != 0;
                layoutResolved = true;
                for (int i = 0; i < Layouts.Length; i++)
                {
                    IntPtr scratch = CreateJobObjectW(IntPtr.Zero, null);
                    if (scratch == IntPtr.Zero)
                        break;
                    bool ok = ApplyLayout(scratch, Layouts[i]);
                    CloseHandle(scratch);
                    if (ok)
                    {
                        layoutSize = Layouts[i].Size;
                        layoutLimitOffset = Layouts[i].ActiveProcessLimitOffset;
                        break;
                    }
                }
                return layoutSize != 0;
            }
        }

        /// <summary>
        /// Creates a tool job with the process limit and kill-on-close set.
        /// Returns IntPtr.Zero when job objects are unusable, in which case the
        /// caller must fall back to killing the tool process directly.
        /// </summary>
        public static IntPtr CreateToolJob()
        {
            if (!EnsureLayout())
                return IntPtr.Zero;

            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            Layout layout = new Layout { Size = layoutSize, ActiveProcessLimitOffset = layoutLimitOffset };
            if (!ApplyLayout(job, layout))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
            return job;
        }

        /// <summary>
        /// Assigns a freshly started tool process to the job. Must run before the
        /// tool can spawn children (tools read their stdin payload first), then
        /// descendants inherit the job automatically.
        /// The return value of AssignProcessToJobObject is deliberately ignored:
        /// on Windows XP it reports failure (win32 1816/5) for assignments that
        /// actually succeeded, so acting on it would kill the tool right away
        /// through KILL_ON_JOB_CLOSE. A real failure costs nothing here, because
        /// cancel/timeout always kills the tool process itself as well.
        /// </summary>
        public static void AssignToJob(IntPtr job, int processId)
        {
            if (job == IntPtr.Zero)
                return;

            IntPtr process = OpenProcess(ProcessAllAccess, false, processId);
            if (process == IntPtr.Zero)
                return;
            try
            {
                AssignProcessToJobObject(job, process);
            }
            finally
            {
                CloseHandle(process);
            }
        }

        /// <summary>
        /// Kills every process in the job (the tool and its whole tree).
        /// No-op for IntPtr.Zero or an already empty job.
        /// </summary>
        public static void KillJobTree(IntPtr job)
        {
            if (job == IntPtr.Zero)
                return;
            try { TerminateJobObject(job, 1); }
            catch { }
        }

        /// <summary>
        /// Closes the job handle. With KILL_ON_JOB_CLOSE this also terminates
        /// anything still inside the job, so it is the final safety net.
        /// </summary>
        public static void CloseJob(IntPtr job)
        {
            if (job == IntPtr.Zero)
                return;
            try { CloseHandle(job); }
            catch { }
        }
    }
}
