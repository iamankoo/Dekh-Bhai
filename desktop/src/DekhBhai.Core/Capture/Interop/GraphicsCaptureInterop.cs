using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.DXGI;
using Vortice.Direct3D11;

namespace DekhBhai.Core.Capture.Interop;

/// <summary>
/// Small, self-contained COM interop shims for consuming Windows.Graphics.Capture and its
/// Direct3D11 interop functions from a classic Win32 desktop app (not UWP/WinUI). These are
/// documented Windows SDK entry points; there is no supported "managed-only" way to reach
/// them, so a small amount of hand-written interop is unavoidable and standard practice
/// (this mirrors Microsoft's own dotnet/WPF screen-capture sample).
/// </summary>
internal static class GraphicsCaptureInterop
{
    private static readonly Guid IGraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IDirect3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    // Modern .NET (CoreCLR) no longer supports MarshalAs(UnmanagedType.HString) in P/Invoke
    // signatures, so HSTRINGs are created/destroyed manually via combase.dll, and the
    // activation factory is fetched as a raw IUnknown pointer rather than an auto-marshaled
    // managed object.
    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern void WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    /// <summary>Creates a GraphicsCaptureItem targeting the whole of the given monitor.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out var hClassName);
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            var interopIid = IGraphicsCaptureItemInteropIid;
            RoGetActivationFactory(hClassName, ref interopIid, out factoryPtr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            var itemIid = GraphicsCaptureItemIid;
            var itemPtr = interop.CreateForMonitor(hmonitor, ref itemIid);
            try
            {
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            WindowsDeleteString(hClassName);
        }
    }

    /// <summary>Wraps a Vortice/DXGI device as the WinRT IDirect3DDevice the capture APIs expect.</summary>
    public static IDirect3DDevice CreateDirect3DDeviceFromDxgiDevice(IDXGIDevice dxgiDevice)
    {
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtDevicePtr);
        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevicePtr);
        }
        finally
        {
            Marshal.Release(winrtDevicePtr);
        }
    }

    /// <summary>
    /// Extracts the underlying ID3D11Texture2D backing a captured Direct3D11CaptureFrame's
    /// WinRT surface, so it can be copied into a CPU-readable staging texture.
    /// </summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        // CsWinRT-projected RCWs don't support a plain C# cast to an arbitrary hand-declared
        // ComImport interface - go through the object's native IUnknown pointer and QI by hand.
        var objRef = ((WinRT.IWinRTObject)surface).NativeObject;
        var accessIid = IDirect3DDxgiInterfaceAccessIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(objRef.ThisPtr, ref accessIid, out var accessPtr));
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetTypedObjectForIUnknown(accessPtr, typeof(IDirect3DDxgiInterfaceAccess));
            var textureIid = typeof(ID3D11Texture2D).GUID;
            var texturePtr = access.GetInterface(ref textureIid);
            // Ownership of the QueryInterface'd reference transfers to the returned wrapper -
            // it must NOT also be released here (that caused a use-after-free after ~8 frames).
            return new ID3D11Texture2D(texturePtr);
        }
        finally
        {
            Marshal.Release(accessPtr);
        }
    }
}
