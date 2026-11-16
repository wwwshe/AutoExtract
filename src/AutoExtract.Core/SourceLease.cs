using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoExtract.Core;

// No write/delete sharing: the downloader cannot modify, replace or rename a leased volume.
public sealed class SourceLease : IDisposable
{
    public string Path { get; }
    public FileStream Stream { get; }
    public SourceLease(string path)
    {
        Path = path;
        var handle = CreateFile(path, 0x80000000 | 0x00010000, 1, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "원본 잠금 실패: " + path); }
        Stream = new FileStream(handle, FileAccess.Read, 128 * 1024, false);
    }
    public void MoveByHandle(string destination)
    {
        // Rename the exact file we tested, without a close/reopen race on the original pathname.
        var bytes = System.Text.Encoding.Unicode.GetBytes(System.IO.Path.GetFullPath(destination));
        var offset = IntPtr.Size == 8 ? 20 : 12;
        var size = offset + bytes.Length + 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, bytes.Length);
            Marshal.Copy(bytes, 0, buffer + offset, bytes.Length);
            if (!SetFileInformationByHandle(Stream.SafeFileHandle, 3, buffer, (uint)size)) { var error = Marshal.GetLastWin32Error(); throw new Win32Exception(error, "검증된 원본의 안전한 이동 실패 (" + error + ")"); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() => Stream.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);
}
