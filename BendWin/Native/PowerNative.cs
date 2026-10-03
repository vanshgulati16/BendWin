using System.Runtime.InteropServices;

namespace BendWin.Native;

internal static class PowerNative
{
    // GUID for lid switch state change power setting notification
    public static readonly Guid GUID_LIDSWITCH_STATE_CHANGE =
        new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient, in Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    // For window-based registration
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    public const uint DEVICE_NOTIFY_SERVICE_HANDLE = 0x00000001;
    // WM_POWERBROADCAST
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;

    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data; // DWORD: 0 = lid closed, 1 = lid opened
    }

    // Monitor functions for getting HMONITOR
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    // RegisterHotKey for global Escape shortcut
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;
    public const uint VK_ESCAPE = 0x1B;
    public const int HOTKEY_ID_PAUSE = 9001;
}
