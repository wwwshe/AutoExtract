using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using AutoExtract.Core;

namespace AutoExtract.App;

public sealed class FileRecycleService : IFileRecycleService
{
    public void Recycle(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("자동 휴지통 이동은 로컬 고정 NTFS 디스크에서만 지원합니다. 원본 유지.");
        Exception? error = null;
        var worker = new Thread(() => { try { RecycleOnSta(path); } catch (Exception e) { error = e; } }) { IsBackground = true, Name = "AutoExtract-Recycle" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); worker.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void RecycleOnSta(string path)
    {
        IShellItem? item = null;
        object? instance = null;
        try
        {
            var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref iid, out item));
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            var operation = (IFileOperation)instance;
            // RECYCLEONDELETE (not legacy ALLOWUNDO), NOERRORUI, EARLYFAILURE,
            // SILENT, NOCONFIRMATION, NOCOPYHOOKS. No permanent-delete fallback.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x400 | 4 | 0x10 | 0x00800000));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item!, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted || File.Exists(path)) throw new IOException("Windows 휴지통 이동이 취소되었거나 완료되지 않았습니다.");
        }
        finally
        {
            if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, out IShellItem? item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    // Unused methods preserve the native COM vtable order.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IntPtr item, IntPtr name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, IntPtr name);
        [PreserveSig] int MoveItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
        [PreserveSig] int CopyItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IntPtr destination, uint attrs, IntPtr name, IntPtr template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
