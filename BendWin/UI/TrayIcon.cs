using System.Windows.Forms;
using BendWin.Core;

namespace BendWin.UI;

// System tray icon with context menu. Left-click shows settings; right-click opens menu.
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly AppModel _model;
    private readonly Action _showSettings;
    private readonly Action _quit;

    public TrayIcon(AppModel model, Action showSettings, Action quit)
    {
        _model        = model;
        _showSettings = showSettings;
        _quit         = quit;

        _notify = new NotifyIcon
        {
            Text    = "BendWin",
            Visible = true,
            Icon    = LoadIcon(),
        };

        var pauseItem = new ToolStripMenuItem("Pause", null, (_, _) =>
        {
            _model.IsPaused = !_model.IsPaused;
            RefreshMenu();
        });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => _showSettings());
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit BendWin", null, (_, _) => _quit());

        menu.Opening += (_, _) => RefreshMenu();

        _notify.ContextMenuStrip = menu;
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _showSettings();
        };

        void RefreshMenu()
        {
            pauseItem.Text = _model.IsPaused ? "Resume" : "Pause";
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "icon.ico");
            if (File.Exists(path)) return new System.Drawing.Icon(path);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void UpdateSensorStatus(string text) => _notify.Text = $"BendWin — {text}";

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
    }
}
