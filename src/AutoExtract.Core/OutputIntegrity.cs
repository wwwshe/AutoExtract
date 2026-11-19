using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutoExtract.Core;

internal static class OutputIntegrity
{
    public static void SetMedium(string path)
    {
        // Update only the mandatory label, preserving audit ACEs and the DACL.
        // No inheritance flags: every output entry is normalized explicitly.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;ME)", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Medium 무결성 설명자 생성 실패");
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "무결성 레이블 읽기 실패");
            var error = SetNamedSecurityInfo(path, 1 /* SE_FILE_OBJECT */, 0x10 /* LABEL_SECURITY_INFORMATION */,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (error != 0) throw new Win32Exception((int)error, "결과물 Medium 무결성 복원 실패: " + path);
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfo(string path, uint objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
