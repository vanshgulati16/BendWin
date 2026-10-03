using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BendWin.Core;
using BendWin.Native;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BendWin.UI;

// Transparent, click-through, always-on-top fullscreen window that displays
// the bend effect. Uses a WPF Image + WriteableBitmap updated at ~60 fps.
internal sealed class OverlayWindow : Window
{
    private readonly BendRenderer _renderer;
    private readonly DesktopCapture _capture;
    private readonly AppModel _model;
    private readonly LidSensor _sensor;

    private WriteableBitmap? _bitmap;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private float _smoothProgress;
    private DateTime _lastTick = DateTime.UtcNow;

    // For power-setting notification (lid events via WndProc)
    private IntPtr _powerNotifyHandle;
    private System.Windows.Interop.HwndSource? _hwndSource;

    public OverlayWindow(BendRenderer renderer, DesktopCapture capture,
        AppModel model, LidSensor sensor)
    {
        _renderer = renderer;
        _capture  = capture;
        _model    = model;
        _sensor   = sensor;

        // Window chrome
        WindowStyle         = WindowStyle.None;
        AllowsTransparency  = true;
        Background          = Brushes.Transparent;
        Topmost             = true;
        ShowInTaskbar       = false;
        ResizeMode          = ResizeMode.NoResize;
        IsHitTestVisible    = false;

        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var img = new System.Windows.Controls.Image
        {
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
        };
        Content = img;

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60 fps
        };
        _timer.Tick += OnTick;

        Loaded   += OnLoaded;
        Closed   += OnClosed;

        // Listen to lid angle changes
        _sensor.AngleChanged += OnAngleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _hwndSource.AddHook(WndProc);

        IntPtr hwnd = _hwndSource.Handle;

        _powerNotifyHandle = PowerNative.RegisterPowerSettingNotification(
            hwnd, PowerNative.GUID_LIDSWITCH_STATE_CHANGE,
            PowerNative.DEVICE_NOTIFY_WINDOW_HANDLE);

        PowerNative.RegisterHotKey(hwnd, PowerNative.HOTKEY_ID_PAUSE, 0, PowerNative.VK_ESCAPE);

        try
        {
            _capture.Start();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Screen capture failed to start:\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "BendWin — Capture Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return; // timer won't start; overlay stays hidden but app keeps running
        }

        int w = _capture.CaptureSize.Width;
        int h = _capture.CaptureSize.Height;

        try
        {
            _renderer.Initialize(w, h);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Renderer failed to initialize:\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "BendWin — Renderer Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return;
        }

        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        ((System.Windows.Controls.Image)Content!).Source = _bitmap;

        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_model.Enabled || _model.IsPaused) { Visibility = Visibility.Hidden; return; }

        float now = (float)(DateTime.UtcNow - _lastTick).TotalSeconds;
        _lastTick = DateTime.UtcNow;

        float angle = _model.FollowLid ? _sensor.CurrentAngle : _model.ManualAngle;
        float target = BendMath.Progress(angle, _model.ClearAngle);
        _smoothProgress = BendMath.Damp(_smoothProgress, target, now);

        if (_smoothProgress < 0.005f) { Visibility = Visibility.Hidden; return; }

        _renderer.SetParams(_smoothProgress,
            _model.Perspective, _model.Blur, _model.Shadow, (float)_model.Style);

        var pixels = _renderer.Draw();
        if (pixels == null) { return; }

        Visibility = Visibility.Visible;

        _bitmap!.Lock();
        unsafe
        {
            var ptr = _bitmap.BackBuffer;
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, ptr, pixels.Length);
        }
        _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
        _bitmap.Unlock();
    }

    private void OnAngleChanged(float angle)
    {
        // LidSensor fires on background thread; schedule a UI tick
        Dispatcher.InvokeAsync(() => { /* tick will pick up the new angle */ });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case PowerNative.WM_POWERBROADCAST when wParam.ToInt32() == PowerNative.PBT_POWERSETTINGCHANGE:
                var setting = Marshal.PtrToStructure<PowerNative.POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == PowerNative.GUID_LIDSWITCH_STATE_CHANGE)
                {
                    bool lidOpen = setting.Data != 0;
                    _sensor.NotifyLidEvent(lidOpen);
                    handled = true;
                }
                break;

            case PowerNative.WM_HOTKEY when wParam.ToInt32() == PowerNative.HOTKEY_ID_PAUSE:
                _model.IsPaused = !_model.IsPaused;
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _sensor.AngleChanged -= OnAngleChanged;
        if (_hwndSource != null)
        {
            PowerNative.UnregisterPowerSettingNotification(_powerNotifyHandle);
            PowerNative.UnregisterHotKey(_hwndSource.Handle, PowerNative.HOTKEY_ID_PAUSE);
            _hwndSource.RemoveHook(WndProc);
        }
        _capture.Stop();
        _renderer.Dispose();
    }
}
