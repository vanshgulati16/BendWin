using System.Diagnostics;
using System.Reflection;
using System.Windows;
using BendWin.Core;

namespace BendWin.UI;

public partial class SettingsWindow : Window
{
    private readonly AppModel _model;
    private readonly LidSensor _sensor;
    private bool _loading = true;

    public SettingsWindow(AppModel model, LidSensor sensor)
    {
        _model  = model;
        _sensor = sensor;
        InitializeComponent();
        LoadFromModel();
        _loading = false;

        // Wire live angle display
        _sensor.AngleChanged += angle =>
            Dispatcher.InvokeAsync(() => LblSensorStatus.Text = SensorStatusText(angle));

        UpdateSensorStatusLabel();
    }

    private void LoadFromModel()
    {
        ChkEnabled.IsChecked    = _model.Enabled;
        ChkStartup.IsChecked    = _model.StartWithWindows;
        ChkFollowLid.IsChecked  = _model.FollowLid;
        ChkSound.IsChecked      = _model.PlaySound;

        SliderPerspective.Value = _model.Perspective * 100;
        SliderBlur.Value        = _model.Blur        * 100;
        SliderShadow.Value      = _model.Shadow      * 100;
        SliderManual.Value      = _model.ManualAngle;
        SliderClear.Value       = _model.ClearAngle;
        SliderAnimDuration.Value = _model.AnimationDuration;

        UpdateManualVisibility();
        UpdateLabels();

        LblVersion.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version}";
    }

    private void UpdateLabels()
    {
        LblPerspective.Text  = $"{(int)SliderPerspective.Value}%";
        LblBlur.Text         = $"{(int)SliderBlur.Value}%";
        LblShadow.Text       = $"{(int)SliderShadow.Value}%";
        LblManualAngle.Text  = $"{(int)SliderManual.Value}°";
        LblClearAngle.Text   = $"{(int)SliderClear.Value}°";
        LblAnimDuration.Text = $"{SliderAnimDuration.Value:F1}s";
        LblPreviewAngle.Text = $"{(int)SliderPreview.Value}°";
    }

    private void UpdateManualVisibility()
    {
        PanelManual.Visibility = _model.FollowLid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSensorStatusLabel()
    {
        Dispatcher.InvokeAsync(() =>
        {
            LblSensorStatus.Text = _sensor.ActiveTier switch
            {
                SensorTier.HidAngle     => $"Angle tracking active — {_sensor.CurrentAngle:F1}°",
                SensorTier.Inclinometer => $"Inclinometer active — {_sensor.CurrentAngle:F1}°",
                _                       => "Using power events (no angle sensor found)",
            };
            LblSensorStatus.Foreground = _sensor.ActiveTier == SensorTier.PowerEvent
                ? System.Windows.Media.Brushes.Orange
                : System.Windows.Media.Brushes.LimeGreen;
        });
    }

    private string SensorStatusText(float angle) => _sensor.ActiveTier switch
    {
        SensorTier.HidAngle     => $"Angle tracking active — {angle:F1}°",
        SensorTier.Inclinometer => $"Inclinometer active — {angle:F1}°",
        _                       => "Using power events (no angle sensor found)",
    };

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnEnabledChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        _model.Enabled = ChkEnabled.IsChecked == true;
        Save();
    }

    private void OnStartupChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        _model.StartWithWindows = ChkStartup.IsChecked == true;
        Save();
    }

    private void OnFollowLidChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        _model.FollowLid = ChkFollowLid.IsChecked == true;
        UpdateManualVisibility();
        Save();
    }

    private void OnSoundChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        _model.PlaySound = ChkSound.IsChecked == true;
        Save();
    }

    private void OnPerspectiveChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _model.Perspective = (float)(SliderPerspective.Value / 100.0);
        LblPerspective.Text = $"{(int)SliderPerspective.Value}%";
        Save();
    }

    private void OnBlurChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _model.Blur = (float)(SliderBlur.Value / 100.0);
        LblBlur.Text = $"{(int)SliderBlur.Value}%";
        Save();
    }

    private void OnShadowChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _model.Shadow = (float)(SliderShadow.Value / 100.0);
        LblShadow.Text = $"{(int)SliderShadow.Value}%";
        Save();
    }

    private void OnManualAngleChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _model.ManualAngle = (float)SliderManual.Value;
        LblManualAngle.Text = $"{(int)SliderManual.Value}°";
        Save();
    }

    private void OnClearAngleChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _model.ClearAngle = (float)SliderClear.Value;
        LblClearAngle.Text = $"{(int)SliderClear.Value}°";
        Save();
    }

    private void OnAnimDurationChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _model.AnimationDuration = (float)SliderAnimDuration.Value;
        LblAnimDuration.Text = $"{SliderAnimDuration.Value:F1}s";
        Save();
    }

    private void OnPreviewAngleChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || LblPreviewAngle == null) return;
        LblPreviewAngle.Text = $"{(int)SliderPreview.Value}°";
        if (!_model.FollowLid)
        {
            _model.ManualAngle = (float)SliderPreview.Value;
            SliderManual.Value = SliderPreview.Value;
        }
    }

    private void OnPresetSilk(object s, RoutedEventArgs e)  { _model.ApplyPreset(BendStyle.Silk);  RefreshSliders(); Save(); }
    private void OnPresetShade(object s, RoutedEventArgs e) { _model.ApplyPreset(BendStyle.Shade); RefreshSliders(); Save(); }
    private void OnPresetFrost(object s, RoutedEventArgs e) { _model.ApplyPreset(BendStyle.Frost); RefreshSliders(); Save(); }

    private void RefreshSliders()
    {
        _loading = true;
        SliderPerspective.Value = _model.Perspective * 100;
        SliderBlur.Value        = _model.Blur        * 100;
        SliderShadow.Value      = _model.Shadow      * 100;
        _loading = false;
        UpdateLabels();
    }

    private void OnResetAppearance(object s, RoutedEventArgs e)
    {
        _model.ResetAppearance();
        RefreshSliders();
        Save();
    }

    private void OnUseCurrentAngle(object s, RoutedEventArgs e)
    {
        float angle = _sensor.CurrentAngle;
        _loading = true;
        SliderClear.Value = Math.Clamp(angle + 10f, 80f, 135f);
        _loading = false;
        _model.ClearAngle = (float)SliderClear.Value;
        LblClearAngle.Text = $"{(int)SliderClear.Value}°";
        Save();
    }

    private void OnCheckUpdates(object s, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/vanshgulati16/BendWin/releases") { UseShellExecute = true });
    }

    private void OnOpenGitHub(object s, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/vanshgulati16/BendWin") { UseShellExecute = true });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true; // hide instead of close
        Hide();
    }

    private void Save() => _model.Save();
}
