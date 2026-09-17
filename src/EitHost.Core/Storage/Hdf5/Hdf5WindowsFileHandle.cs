using System.ComponentModel;
using System.Runtime.InteropServices;
using HDF.PInvoke;

namespace EitHost.Core.Storage.Hdf5;

internal static class Hdf5WindowsFileHandle
{
    internal static void PreventInheritance(long file)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Our Windows FAPL explicitly selects SEC2. Its VFD handle points to an
        // int CRT descriptor; the bundled HDF5 library uses the Universal CRT.
        // Do not close this borrowed handle: HDF5 retains ownership.
        var descriptorPointer = IntPtr.Zero;
        if (H5F.get_vfd_handle(file, H5P.DEFAULT, ref descriptorPointer) < 0
            || descriptorPointer == IntPtr.Zero)
            throw new IOException("Cannot obtain the native HDF5 file descriptor.");

        var handle = GetOsFileHandle(Marshal.ReadInt32(descriptorPointer));
        if (handle == new IntPtr(-1))
            throw new IOException("Cannot obtain the Windows handle for the HDF5 SEC2 descriptor.");
        if (!SetHandleInformation(handle, 1, 0)) // HANDLE_FLAG_INHERIT
            throw new IOException("Cannot prevent a child process from inheriting the HDF5 file.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_get_osfhandle", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetOsFileHandle(int descriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
