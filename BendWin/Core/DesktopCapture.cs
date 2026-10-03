using System.Runtime.InteropServices;
using BendWin.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
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
        _item = CreateCaptureItemForPrimaryMonitor(); // throws with detail on failure

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
            // Unwrap WinRT surface → D3D11 texture via IDxgiInterfaceAccess
            var surfacePtr = Marshal.GetIUnknownForObject(frame.Surface);
            var access = (IDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(surfacePtr);
            Marshal.Release(surfacePtr);
            Guid tex2dGuid = typeof(ID3D11Texture2D).GUID;
            access.GetInterface(ref tex2dGuid, out var texPtr);
            using var srcTexture = new ID3D11Texture2D(texPtr);

            var desc = srcTexture.Description;
            desc.BindFlags = BindFlags.ShaderResource;
            desc.MiscFlags = ResourceOptionFlags.None;
            desc.Usage = ResourceUsage.Default;
            desc.CPUAccessFlags = CpuAccessFlags.None;

            var copy = _device.CreateTexture2D(desc);
            lock (_context) { _context.CopyResource(copy, srcTexture); }

            _store.Set(copy, DateTime.UtcNow.Ticks);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BendWin] FrameArrived error: {ex}");
        }
    }

    // ── WinRT interop ─────────────────────────────────────────────────────────

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
    private static extern int NativeCreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    private static IDirect3DDevice CreateWinRTDevice(ID3D11Device d3d11)
    {
        using var dxgi = d3d11.QueryInterface<IDXGIDevice>();
        int hr = NativeCreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr);
        Marshal.ThrowExceptionForHR(hr);
        var dev = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
        Marshal.Release(ptr);
        return dev;
    }

    // IID for Windows.Graphics.Capture.IGraphicsCaptureItem
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // IDirect3DDxgiInterfaceAccess — lets us unwrap the WinRT surface to a D3D11 texture
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out IntPtr ppv);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string str, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    private static GraphicsCaptureItem CreateCaptureItemForPrimaryMonitor()
    {
        var hMonitor = PowerNative.MonitorFromPoint(
            new PowerNative.POINT { X = 0, Y = 0 },
            PowerNative.MONITOR_DEFAULTTOPRIMARY);

        if (hMonitor == IntPtr.Zero)
            throw new Exception("MonitorFromPoint returned null — no primary monitor found.");

        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hstrHr = WindowsCreateString(classId, classId.Length, out var hstr);
        if (hstrHr < 0) Marshal.ThrowExceptionForHR(hstrHr);

        Guid interopIID = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        int hr;
        try
        {
            hr = RoGetActivationFactory(hstr, ref interopIID, out var factoryPtr);

            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            Guid inspectableIID = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
            hr = interop.CreateForMonitor(hMonitor, ref inspectableIID, out var itemPtr);

            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            var item = WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }
        finally
        {
            WindowsDeleteString(hstr);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
