using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MigrationCore.Services;

// 先挂起创建，再加入独立 Job，最后恢复，消除启动后尚未登记的子进程窗口。
internal sealed class OwnedProcessJob : IDisposable
{
    private readonly SafeFileHandle _job;
    private AnonymousPipeServerStream? _stdout;
    private AnonymousPipeServerStream? _stderr;
    private Process? _process;
    private SafeFileHandle? _thread;
    private bool _created;
    private bool _assigned;
    private bool _startupExited;
    public int? Id { get; private set; }
    public StreamReader Output { get; private set; } = null!;
    public StreamReader Error { get; private set; } = null!;
    public int ExitCode => _process?.ExitCode ?? throw new IOException("进程未启动。");
    public bool HasExited => _process?.HasExited ?? (!_created || _startupExited);
    public uint ActiveProcesses
    {
        get
        {
            if (!QueryInformationJobObject(_job, 1, out Accounting data, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认本任务进程集。");
            return data.ActiveProcesses;
        }
    }

    public OwnedProcessJob()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建本任务 Job。");
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            int error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error, "无法设置进程归属保护。");
        }
    }

    public void Start(string executable, IEnumerable<string> arguments, string workingDirectory, Encoding encoding)
    {
        if (_created) throw new InvalidOperationException("一个 Job 只允许启动一次根进程。");
        _stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        _stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        using SafeFileHandle input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
        if (input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法准备子进程标准输入。");
        UIntPtr size = UIntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == UIntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr attributes = Marshal.AllocHGlobal(checked((IntPtr)(long)size.ToUInt64()));
        IntPtr handles = IntPtr.Zero;
        bool initialized = false;
        ProcessInformation process = default;
        try
        {
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, _stdout.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, _stderr.ClientSafePipeHandle.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles,
                    new UIntPtr((uint)(3 * IntPtr.Size)), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法限定继承的标准流句柄。");
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = input.DangerousGetHandle(), Output = _stdout.ClientSafePipeHandle.DangerousGetHandle(),
                    Error = _stderr.ClientSafePipeHandle.DangerousGetHandle()
                },
                Attributes = attributes
            };
            var command = new StringBuilder();
            AppendArgument(command, executable);
            foreach (string argument in arguments) { command.Append(' '); AppendArgument(command, argument); }
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x4 | 0x08000000 | 0x00080000, IntPtr.Zero, workingDirectory, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建复制进程。");
            _created = true;
            Id = checked((int)process.ProcessId);
            _process = Process.GetProcessById(Id.Value);
            _ = _process.SafeHandle;
            if (!AssignProcessToJobObject(_job, process.Process)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法登记复制进程归属。");
            _assigned = true;
            Output = new StreamReader(_stdout, encoding, true, 4096, leaveOpen: true);
            Error = new StreamReader(_stderr, encoding, true, 4096, leaveOpen: true);
            _thread = new SafeFileHandle(process.Thread, ownsHandle: true);
            process.Thread = IntPtr.Zero;
        }
        catch (Exception startError)
        {
            if (_created)
            {
                bool stopped = _assigned ? TerminateJobObject(_job, 1) : TerminateProcess(process.Process, 1);
                _startupExited = stopped && WaitForSingleObject(process.Process, uint.MaxValue) == 0;
                if (!_startupExited) throw new AggregateException(startError, new IOException("无法确认启动失败的本任务进程已经退出。"));
            }
            throw;
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handles);
            _stdout.DisposeLocalCopyOfClientHandle();
            _stderr.DisposeLocalCopyOfClientHandle();
        }
    }

    public void Resume()
    {
        SafeFileHandle thread = _thread ?? throw new InvalidOperationException("没有待恢复的本任务线程。");
        try
        {
            if (ResumeThread(thread) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复本任务复制进程。");
        }
        finally { thread.Dispose(); _thread = null; }
    }

    public void Stop()
    {
        if (_assigned && !TerminateJobObject(_job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法结束本任务进程集。");
        _thread?.Dispose();
        _thread = null;
        if (_created && !_assigned && !HasExited) throw new IOException("进程未能登记归属，退出状态待人工核实。");
    }

    public async Task WaitForExitAsync(CancellationToken ct)
    {
        if (_process is not null) await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        else if (!HasExited) throw new IOException("没有可核验的本任务进程句柄。");
        while (ActiveProcesses != 0) await Task.Delay(10, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Output?.Dispose(); Error?.Dispose();
        _stdout?.Dispose(); _stderr?.Dispose();
        _thread?.Dispose();
        _process?.Dispose(); _job.Dispose();
    }

    private static void AppendArgument(StringBuilder command, string argument)
    {
        command.Append('"');
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            command.Append('\\', c == '"' ? 2 * slashes + 1 : slashes);
            command.Append(c);
            slashes = 0;
        }
        command.Append('\\', 2 * slashes).Append('"');
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public uint Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits data, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting data, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
