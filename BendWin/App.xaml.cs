using System.Threading;
using System.Windows;
using BendWin.Core;
using BendWin.UI;

namespace BendWin;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settings;
    private AppModel? _model;
    private LidSensor? _sensor;
    private BendRenderer? _renderer;
    private DesktopCapture? _capture;
    private Core.FrameStore? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard
        _mutex = new Mutex(true, "BendWin_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        _model    = AppModel.Load();
        _store    = new Core.FrameStore();
        _renderer = new BendRenderer(_store);
        _capture  = new DesktopCapture(_renderer.Device, _renderer.Context, _store);
        _sensor   = new LidSensor(_model);
        _sensor.Start();

        _settings = new SettingsWindow(_model, _sensor);
        _overlay  = new OverlayWindow(_renderer, _capture, _model, _sensor);
        _overlay.Show();

        _tray = new TrayIcon(_model,
            showSettings: () => { _settings.Show(); _settings.Activate(); },
            quit: () => ExplicitShutdown());

        // Update tray tooltip with sensor mode
        _sensor.AngleChanged += _ =>
        {
            string status = _sensor.ActiveTier switch
            {
                SensorTier.HidAngle     => $"{_sensor.CurrentAngle:F0}°",
                SensorTier.Inclinometer => $"~{_sensor.CurrentAngle:F0}°",
                _                       => "power events",
            };
            _tray.UpdateSensorStatus(status);
        };
    }

    private void ExplicitShutdown()
    {
        _model?.Save();
        _tray?.Dispose();
        _sensor?.Dispose();
        _capture?.Dispose();
        _renderer?.Dispose();
        _store?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _model?.Save();
        base.OnExit(e);
    }
}
