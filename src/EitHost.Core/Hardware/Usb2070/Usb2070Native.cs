using System.Runtime.InteropServices;

namespace EitHost.Core.Hardware.Usb2070;

public static partial class Usb2070Native
{
    public static nint InvalidHandleValue { get; } = new(-1);

    [DllImport(Usb2070Constants.NativeLibraryName, EntryPoint = "USB2070_Link", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    public static extern nint Link(byte deviceNumber);

    [DllImport(Usb2070Constants.NativeLibraryName, EntryPoint = "USB2070_UnLink", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnLink(nint deviceHandle);

    [DllImport(Usb2070Constants.NativeLibraryName, EntryPoint = "USB2070_InitAD", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitAd(nint deviceHandle, ref Usb2070AdParameters parameters);

    [DllImport(Usb2070Constants.NativeLibraryName, EntryPoint = "USB2070_ReadAD", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadAd(nint deviceHandle, [Out] ushort[] buffer, uint count);

    [DllImport(Usb2070Constants.NativeLibraryName, EntryPoint = "USB2070_StopAD", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StopAd(nint deviceHandle, byte deviceNumber);

    [DllImport(Usb2070Constants.NativeLibraryName, EntryPoint = "USB2070_GetBufOver", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetBufferOverflow(nint deviceHandle, out int bufferOverflow);

    [DllImport(Usb2070Constants.NativeLibraryName, EntryPoint = "USB2070_ExeSoftTrig", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ExecuteSoftTrigger(nint deviceHandle);

    [DllImport(Usb2070Constants.NativeLibraryName, EntryPoint = "USB2070_GetDevInfo", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDeviceInfo(nint deviceHandle, out Usb2070CardInfo cardInfo);
}
