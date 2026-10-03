using BendWin.Native;
using System.Runtime.InteropServices;

namespace BendWin.Core;

public enum SensorTier { HidAngle, Inclinometer, PowerEvent }

// Detects lid angle using three fallback tiers.
// Reports angle in degrees (0=closed, 135=fully open) via the AngleChanged event.
public sealed class LidSensor : IDisposable
{
    public event Action<float>? AngleChanged;
    public SensorTier ActiveTier { get; private set; } = SensorTier.PowerEvent;
    public float CurrentAngle { get; private set; } = 135f;

    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // For power-event animation
    private readonly AppModel _model;
    private System.Threading.Timer? _animTimer;
    private float _animTarget = 135f;
    private float _animCurrent = 135f;
    private DateTime _animLastTick;

    // For inclinometer
    private Windows.Devices.Sensors.Inclinometer? _inclinometer;

    public LidSensor(AppModel model)
    {
        _model = model;
    }

    public void Start()
    {
        if (TryStartHid()) return;
        if (TryStartInclinometer()) return;
        StartPowerEventFallback();
    }

    // ── Tier 1: HID angle sensor ─────────────────────────────────────────────

    private bool TryStartHid()
    {
        var handle = FindLidHidDevice();
        if (handle == IntPtr.Zero || handle == HidNative.INVALID_HANDLE_VALUE) return false;

        ActiveTier = SensorTier.HidAngle;
        var token = _cts.Token;
        Thread t = new(() => HidPollLoop(handle, token)) { IsBackground = true, Name = "LidSensor-HID" };
        t.Start();
        return true;
    }

    private static IntPtr FindLidHidDevice()
    {
        HidNative.HidD_GetHidGuid(out var hidGuid);
        IntPtr devInfo = HidNative.SetupDiGetClassDevs(
            hidGuid, null, IntPtr.Zero,
            HidNative.DIGCF_PRESENT | HidNative.DIGCF_DEVICEINTERFACE);
        if (devInfo == IntPtr.Zero) return IntPtr.Zero;

        try
        {
            uint index = 0;
            while (true)
            {
                var idata = new HidNative.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<HidNative.SP_DEVICE_INTERFACE_DATA>() };
                if (!HidNative.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, hidGuid, index++, ref idata)) break;

                HidNative.SetupDiGetDeviceInterfaceDetail(devInfo, ref idata, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed == 0) continue;

                IntPtr detailData = Marshal.AllocHGlobal((int)needed);
                Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6); // cbSize
                try
                {
                    if (!HidNative.SetupDiGetDeviceInterfaceDetail(devInfo, ref idata, detailData, needed, out _, IntPtr.Zero)) continue;
                    // DevicePath starts 4 bytes into the struct
                    string path = Marshal.PtrToStringAuto(detailData + 4) ?? "";

                    IntPtr hDev = HidNative.CreateFile(path,
                        HidNative.GENERIC_READ,
                        HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                        IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
                    if (hDev == HidNative.INVALID_HANDLE_VALUE) continue;

                    var attrs = new HidNative.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HidNative.HIDD_ATTRIBUTES>() };
                    if (!HidNative.HidD_GetAttributes(hDev, ref attrs)) { HidNative.CloseHandle(hDev); continue; }

                    // Look for devices that expose usage page 0x20, usage 0x8A (generic lid sensors)
                    // or any OEM lid-angle HID (usage page 0xFF00-0xFFFF)
                    if (HidNative.HidD_GetPreparsedData(hDev, out var preparsed))
                    {
                        HidNative.HidP_GetCaps(preparsed, out var caps);
                        HidNative.HidD_FreePreparsedData(preparsed);

                        bool isLidSensor = (caps.UsagePage == 0x0020 && caps.Usage == 0x008A)
                                        || (caps.UsagePage >= 0xFF00 && caps.FeatureReportByteLength >= 4);
                        if (isLidSensor) return hDev;
                    }

                    HidNative.CloseHandle(hDev);
                }
                finally { Marshal.FreeHGlobal(detailData); }
            }
        }
        finally { HidNative.SetupDiDestroyDeviceInfoList(devInfo); }

        return IntPtr.Zero;
    }

    private void HidPollLoop(IntPtr handle, CancellationToken token)
    {
        int pollMs = 16;
        var buf = new byte[64];
        buf[0] = 0; // report ID

        while (!token.IsCancellationRequested)
        {
            if (HidNative.HidD_GetFeature(handle, buf, (uint)buf.Length))
            {
                // Bytes 1-2: little-endian angle in tenths of a degree (similar to Apple sensor)
                int raw = buf[1] | (buf[2] << 8);
                float angle = raw / 10f;
                if (angle is >= 0f and <= 180f)
                {
                    CurrentAngle = angle;
                    AngleChanged?.Invoke(angle);
                    pollMs = CurrentAngle < 30f ? 250 : (CurrentAngle < 100f ? 100 : 16);
                }
            }
            Thread.Sleep(pollMs);
        }
        HidNative.CloseHandle(handle);
    }

    // ── Tier 2: Windows Inclinometer ─────────────────────────────────────────

    private bool TryStartInclinometer()
    {
        try
        {
            _inclinometer = Windows.Devices.Sensors.Inclinometer.GetDefault();
            if (_inclinometer == null) return false;

            _inclinometer.ReportInterval = Math.Max(_inclinometer.MinimumReportInterval, 50u);
            _inclinometer.ReadingChanged += OnInclinometerReading;
            ActiveTier = SensorTier.Inclinometer;
            return true;
        }
        catch { return false; }
    }

    private void OnInclinometerReading(Windows.Devices.Sensors.Inclinometer sender,
        Windows.Devices.Sensors.InclinometerReadingChangedEventArgs args)
    {
        // Pitch: negative = screen tilting back (closing)
        // Map pitch [-90°, 0°] → lid angle [0°, 90°]; flat = 90° open
        float pitch = args.Reading.PitchDegrees;
        float angle = Math.Clamp(90f + pitch, 0f, 135f);
        CurrentAngle = angle;
        AngleChanged?.Invoke(angle);
    }

    // ── Tier 3: Power event fallback ─────────────────────────────────────────

    private void StartPowerEventFallback()
    {
        ActiveTier = SensorTier.PowerEvent;
        // Actual WM_POWERBROADCAST is received by OverlayHost's Win32 window.
        // The app calls NotifyLidEvent() directly.
        CurrentAngle = 135f;
    }

    // Called by the overlay host's WndProc when a lid power event arrives.
    public void NotifyLidEvent(bool lidOpen)
    {
        _animTarget = lidOpen ? 135f : 0f;
        _animCurrent = CurrentAngle;
        _animLastTick = DateTime.UtcNow;

        _animTimer?.Dispose();
        _animTimer = new System.Threading.Timer(_ => AnimationTick(), null, 0, 16);
    }

    private void AnimationTick()
    {
        float dt = (float)(DateTime.UtcNow - _animLastTick).TotalSeconds;
        _animLastTick = DateTime.UtcNow;

        float speed = 1f / Math.Max(0.1f, _model.AnimationDuration);
        float step = speed * dt;
        _animCurrent = _animTarget > _animCurrent
            ? Math.Min(_animCurrent + step * 135f, _animTarget)
            : Math.Max(_animCurrent - step * 135f, _animTarget);

        CurrentAngle = _animCurrent;
        AngleChanged?.Invoke(_animCurrent);

        if (Math.Abs(_animCurrent - _animTarget) < 0.5f)
        {
            _animTimer?.Dispose();
            _animTimer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _animTimer?.Dispose();
        if (_inclinometer != null)
            _inclinometer.ReadingChanged -= OnInclinometerReading;
    }
}
