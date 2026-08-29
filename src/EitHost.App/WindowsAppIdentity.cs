using System.Runtime.InteropServices;

namespace EitHost.App;

internal static class WindowsAppIdentity
{
    private static readonly Guid PropertyStoreInterfaceId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    internal const string AppUserModelId = "EitHost.Workstation";

    /// <summary>
    /// Managed size of the PROPVARIANT interop struct, exposed so tests can pin it to the
    /// native size the shell expects.
    /// </summary>
    internal static int PropVariantSize => Marshal.SizeOf<PropVariant>();

    internal static void Apply()
    {
        Apply(SetCurrentProcessExplicitAppUserModelID);
    }

    internal static void Apply(Func<string, int> assignAppUserModelId)
    {
        ArgumentNullException.ThrowIfNull(assignAppUserModelId);

        var hResult = assignAppUserModelId(AppUserModelId);
        ThrowIfNotOk(hResult, nameof(SetCurrentProcessExplicitAppUserModelID));
    }

    internal static IDisposable ApplyToWindow(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        var interfaceId = PropertyStoreInterfaceId;
        var hResult = SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out var propertyStore);
        ThrowIfNotOk(hResult, nameof(SHGetPropertyStoreForWindow));

        try
        {
            var value = new PropVariant(AppUserModelId);
            try
            {
                var key = AppUserModelIdKey;
                hResult = propertyStore.SetValue(ref key, ref value);
                ThrowIfNotOk(hResult, "IPropertyStore.SetValue");
            }
            finally
            {
                value.Dispose();
            }

            return new WindowIdentityRegistration(propertyStore);
        }
        catch
        {
            Marshal.FinalReleaseComObject(propertyStore);
            throw;
        }
    }

    private static void ThrowIfNotOk(int hResult, string operation)
    {
        if (hResult == 0)
        {
            return;
        }

        Marshal.ThrowExceptionForHR(hResult);
        throw new InvalidOperationException(
            $"{operation} returned unexpected HRESULT 0x{hResult:X8}.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(
        nint windowHandle,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant propVariant);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        internal readonly Guid FormatId = formatId;
        internal readonly uint PropertyId = propertyId;
    }

    /// <summary>
    /// Mirrors the native PROPVARIANT layout: an 8-byte header followed by a union whose
    /// largest member is two pointers wide. The trailing members are never read by managed
    /// code, but they must exist so the struct matches the native size (24 bytes on x64,
    /// 16 bytes on x86); otherwise <see cref="PropVariantClear"/> writes past the struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant : IDisposable
    {
        private ushort variantType;
        private readonly ushort reserved1;
        private readonly ushort reserved2;
        private readonly ushort reserved3;
        private nint pointerValue;
        private readonly nint unionTail;

        internal PropVariant(string value)
        {
            variantType = (ushort)VarEnum.VT_LPWSTR;
            reserved1 = 0;
            reserved2 = 0;
            reserved3 = 0;
            pointerValue = Marshal.StringToCoTaskMemUni(value);
            unionTail = 0;
        }

        public void Dispose()
        {
            _ = PropVariantClear(ref this);
        }
    }

    private sealed class WindowIdentityRegistration(IPropertyStore propertyStore) : IDisposable
    {
        private IPropertyStore? propertyStore = propertyStore;

        public void Dispose()
        {
            var store = Interlocked.Exchange(ref propertyStore, null);
            if (store is null)
            {
                return;
            }

            try
            {
                var key = AppUserModelIdKey;
                var empty = default(PropVariant);
                _ = store.SetValue(ref key, ref empty);
            }
            finally
            {
                Marshal.FinalReleaseComObject(store);
            }
        }
    }
}
