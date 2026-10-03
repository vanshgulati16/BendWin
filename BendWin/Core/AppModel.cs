using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BendWin.Core;

public enum BendStyle { Silk = 0, Shade = 1, Frost = 2 }
public enum LidMode { Sensor, PowerEvent }

public sealed class AppModel : INotifyPropertyChanged
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BendWin", "settings.json");

    // ── Appearance ──────────────────────────────────────────────────────────
    private BendStyle _style = BendStyle.Silk;
    public BendStyle Style { get => _style; set => Set(ref _style, value); }

    private float _perspective = 0.35f;
    public float Perspective { get => _perspective; set => Set(ref _perspective, Math.Clamp(value, 0f, 1f)); }

    private float _blur = 0.9f;
    public float Blur { get => _blur; set => Set(ref _blur, Math.Clamp(value, 0f, 1f)); }

    private float _shadow = 0.35f;
    public float Shadow { get => _shadow; set => Set(ref _shadow, Math.Clamp(value, 0f, 1f)); }

    // ── Lid behaviour ────────────────────────────────────────────────────────
    private bool _followLid = true;
    public bool FollowLid { get => _followLid; set => Set(ref _followLid, value); }

    private float _manualAngle = 90f;
    public float ManualAngle { get => _manualAngle; set => Set(ref _manualAngle, Math.Clamp(value, 12f, 135f)); }

    private float _clearAngle = 110f;
    public float ClearAngle { get => _clearAngle; set => Set(ref _clearAngle, Math.Clamp(value, 80f, 135f)); }

    private float _animationDuration = 0.6f;
    // Seconds used for power-event animation when no continuous sensor is available.
    public float AnimationDuration { get => _animationDuration; set => Set(ref _animationDuration, Math.Clamp(value, 0.2f, 2f)); }

    private bool _playSound = true;
    public bool PlaySound { get => _playSound; set => Set(ref _playSound, value); }

    // ── General ──────────────────────────────────────────────────────────────
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private bool _startWithWindows = false;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (Set(ref _startWithWindows, value))
                ApplyStartWithWindows(value);
        }
    }

    // ── Runtime (not persisted) ───────────────────────────────────────────────
    [JsonIgnore] public float LiveAngle { get; set; } = 135f;
    [JsonIgnore] public LidMode ActiveLidMode { get; set; } = LidMode.PowerEvent;
    [JsonIgnore] public bool IsPaused { get; set; } = false;

    // ── Presets ──────────────────────────────────────────────────────────────
    public void ApplyPreset(BendStyle preset)
    {
        Style = preset;
        switch (preset)
        {
            case BendStyle.Silk:  Perspective = 0.35f; Blur = 0.90f; Shadow = 0.35f; break;
            case BendStyle.Shade: Perspective = 0.45f; Blur = 0.70f; Shadow = 0.55f; break;
            case BendStyle.Frost: Perspective = 0.25f; Blur = 0.95f; Shadow = 0.20f; break;
        }
    }

    public void ResetAppearance() => ApplyPreset(BendStyle.Silk);

    // ── Persistence ──────────────────────────────────────────────────────────
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, _jsonOptions));
        }
        catch { }
    }

    public static AppModel Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var model = JsonSerializer.Deserialize<AppModel>(File.ReadAllText(SettingsPath), _jsonOptions);
                if (model != null) return model;
            }
        }
        catch { }
        return new AppModel();
    }

    private static void ApplyStartWithWindows(bool enable)
    {
        const string key = "BendWin";
        using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (run == null) return;
        if (enable)
            run.SetValue(key, $"\"{Environment.ProcessPath}\"");
        else
            run.DeleteValue(key, throwOnMissingValue: false);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
