using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutoExtract.Core;

/// <summary>No-capability AppContainer: writes require the private workspace ACL.</summary>
public static class RestrictedProcess
{
    public static async Task<int> RunAsync(string executable, IEnumerable<string> args, string workspace, CancellationToken token, string? logPath = null, string? writableDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        ArchiveDetectionService.RejectReparseAncestors(workspace);
        var name = "AutoExtract.Worker." + Guid.NewGuid().ToString("N");
        var hr = CreateAppContainerProfile(name, name, "Isolated archive worker", IntPtr.Zero, 0, out var sid);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        IntPtr attributes = IntPtr.Zero, capabilitiesPtr = IntPtr.Zero, handleList = IntPtr.Zero;
        try
        {
            var directory = new DirectoryInfo(workspace);
            var acl = directory.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            var writable = Path.GetFullPath(writableDirectory ?? workspace);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            if (!writable.Equals(root, StringComparison.OrdinalIgnoreCase) && !writable.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("쓰기 허용 폴더가 작업 경로 밖에 있습니다.");
            Directory.CreateDirectory(writable);
            ArchiveDetectionService.RejectReparseAncestors(writable);
            var writableInfo = new DirectoryInfo(writable);
            var writableAcl = writableInfo.GetAccessControl();
            writableAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            writableInfo.SetAccessControl(writableAcl);
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            using var output = CreateFile(logPath ?? Path.Combine(workspace, "process.log"), 0x40000000, 1, ref security, 2, 0x80, IntPtr.Zero);
            using var input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0x80, IntPtr.Zero);
            if (output.IsInvalid || input.IsInvalid) throw Error("반디집 로그 핸들 생성");
            var caps = new SecurityCapabilities { AppContainerSid = sid };
            capabilitiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>()); Marshal.StructureToPtr(caps, capabilitiesPtr, false);
            nuint size = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal((int)size);
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw Error("격리 속성 생성");
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, capabilitiesPtr, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero)) throw Error("AppContainer 설정");
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handleList, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, output.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handleList, (nuint)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero)) throw Error("로그 핸들 상속 설정");
            using var job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobExtended { Basic = new JobBasic { LimitFlags = 0x2000 | 8, ActiveProcessLimit = 1 } };
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtended>())) throw Error("작업 개체 제한 설정");
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, StdInput = input.DangerousGetHandle(), StdOutput = output.DangerousGetHandle(), StdError = output.DangerousGetHandle() }, Attributes = attributes };
            var command = new StringBuilder(Quote(executable) + " " + string.Join(' ', args.Select(Quote)));
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000 | 4 | 0x80000, IntPtr.Zero, workspace, ref startup, out var pi)) throw Error("격리된 반디집 실행");
            using var processHandle = new SafeFileHandle(pi.Process, true);
            using var threadHandle = new SafeFileHandle(pi.Thread, true);
            try
            {
                if (!AssignProcessToJobObject(job, processHandle)) throw Error("프로세스 작업 개체 연결");
                if (ResumeThread(threadHandle) == uint.MaxValue) throw Error("프로세스 시작");
                using var process = Process.GetProcessById(pi.ProcessId);
                using var cancel = token.Register(() => TerminateJobObject(job, 1223));
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!GetExitCodeProcess(processHandle, out var code)) throw Error("종료 코드 읽기");
                return unchecked((int)code);
            }
            finally { TerminateProcess(processHandle, 1223); WaitForSingleObject(processHandle, 10000); }
        }
        finally
        {
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (capabilitiesPtr != IntPtr.Zero) Marshal.FreeHGlobal(capabilitiesPtr);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            FreeSid(sid); DeleteAppContainerProfile(name);
        }
    }
    private static string Quote(string text)
    {
        var b = new StringBuilder("\""); var slashes = 0;
        foreach (var c in text)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append(c); }
            else { b.Append('\\', slashes); b.Append(c); }
            slashes = 0;
        }
        b.Append('\\', slashes * 2); return b.Append('"').ToString();
    }
    private static Win32Exception Error(string context) => new(Marshal.GetLastWin32Error(), context);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public IntPtr ReservedPtr, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasic
    { public long PerProcessTime, PerJobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtended
    { public JobBasic Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int cls, ref JobExtended info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
}
