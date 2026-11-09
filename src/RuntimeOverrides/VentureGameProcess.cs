using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VirtualDesktop.LibraryOverrides
{
    internal sealed class VentureGameProcess : IDisposable
    {
        private readonly SafeFileHandle job;
        internal Process Process { get; private set; }

        private VentureGameProcess(SafeFileHandle job, Process process)
        {
            this.job = job;
            Process = process;
        }

        internal static VentureGameProcess Start(string launcher, string arguments)
        {
            const uint createSuspended = 0x4;
            const uint killOnJobClose = 0x2000;
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(); }
            Process process = null;
            ProcessInformation child = new ProcessInformation();
            try
            {
                var limits = new ExtendedLimits();
                limits.Basic.Flags = killOnJobClose;
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                    throw new Win32Exception();
                var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)) };
                var command = new StringBuilder("\"" + launcher + "\" " + arguments);
                if (!CreateProcess(launcher, command, IntPtr.Zero, IntPtr.Zero, false, createSuspended,
                    IntPtr.Zero, System.IO.Path.GetDirectoryName(launcher), ref startup, out child))
                    throw new Win32Exception();
                if (!AssignProcessToJobObject(job, child.ProcessHandle)) throw new Win32Exception();
                process = Process.GetProcessById(child.ProcessId);
                if (ResumeThread(child.ThreadHandle) == UInt32.MaxValue) throw new Win32Exception();
                return new VentureGameProcess(job, process);
            }
            catch
            {
                if (child.ProcessHandle != IntPtr.Zero) TerminateProcess(child.ProcessHandle, 1);
                if (process != null) process.Dispose();
                job.Dispose();
                throw;
            }
            finally
            {
                if (child.ThreadHandle != IntPtr.Zero) CloseHandle(child.ThreadHandle);
                if (child.ProcessHandle != IntPtr.Zero) CloseHandle(child.ProcessHandle);
            }
        }

        public void Dispose()
        {
            job.Dispose();
            Process.Dispose();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedBytes;
            public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr ProcessHandle, ThreadHandle;
            public int ProcessId, ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
