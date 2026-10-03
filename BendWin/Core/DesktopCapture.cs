using System.Runtime.InteropServices;
using BendWin.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace BendWin.Core;

// Captures the primary monitor using Windows.Graphics.Capture and writes
// ID3D11Texture2D copies into FrameStore.
public sealed class DesktopCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly FrameStore _store;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _winrtDevice;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public SizeInt32 CaptureSize { get; private set; }

    public DesktopCapture(ID3D11Device device, ID3D11DeviceContext context, FrameStore store)
    {
        _device = device;
        _context = context;
        _store = store;
    }

    public void Start()
    {
        if (IsRunning) return;

        _winrtDevice = CreateWinRTDevice(_device);
        _item = CreateCaptureItemForPrimaryMonitor();
        if (_item == null) throw new InvalidOperationException("Cannot create capture item for primary monitor.");

        CaptureSize = _item.Size;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _item.Size);

        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        try
        {
            // Get the underlying D3D11 texture from the WinRT surface
            var access = (IDirect3DDxgiInterfaceAccess)frame.Surface;
            Guid tex2dGuid = typeof(ID3D11Texture2D).GUID;
            using var srcTexture = (ID3D11Texture2D)access.GetInterface(tex2dGuid);

            var desc = srcTexture.Description;
            desc.BindFlags = BindFlags.ShaderResource;
            desc.MiscFlags = ResourceOptionFlags.None;
            desc.Usage = ResourceUsage.Default;
            desc.CPUAccessFlags = CpuAccessFlags.None;

            var copy = _device.CreateTexture2D(desc);
            lock (_context) { _context.CopyResource(copy, srcTexture); }

            _store.Set(copy, DateTime.UtcNow.Ticks);
        }
        catch { }
    }

    // ── WinRT interop ─────────────────────────────────────────────────────────

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
    private static extern void NativeCreateDirect3D11DeviceFromDXGIDevice(
        [MarshalAs(UnmanagedType.IUnknown)] object dxgiDevice,
        out IntPtr graphicsDevice);

    private static IDirect3DDevice CreateWinRTDevice(ID3D11Device d3d11)
    {
        var dxgi = d3d11.QueryInterface<IDXGIDevice>();
        NativeCreateDirect3D11DeviceFromDXGIDevice(dxgi, out var ptr);
        var dev = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
        Marshal.Release(ptr);
        return dev;
    }

    // IID for Windows.Graphics.Capture.IGraphicsCaptureItem
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string classId,
        ref Guid iid,
        out IntPtr factory);

    private static GraphicsCaptureItem? CreateCaptureItemForPrimaryMonitor()
    {
        try
        {
            var hMonitor = PowerNative.MonitorFromPoint(
                new PowerNative.POINT { X = 0, Y = 0 },
                PowerNative.MONITOR_DEFAULTTOPRIMARY);

            Guid interopIID = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            int hr = RoGetActivationFactory(
                "Windows.Graphics.Capture.GraphicsCaptureItem",
                ref interopIID,
                out var factoryPtr);
            if (hr < 0) return null;

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            Guid itemIID = IID_IGraphicsCaptureItem;
            hr = interop.CreateForMonitor(hMonitor, ref itemIID, out var itemPtr);
            if (hr < 0) return null;

            var item = WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
