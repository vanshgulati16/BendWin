using System.Drawing;
using System.Windows.Forms;
using BendWin.Core;

namespace BendWin.UI;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly AppModel _model;
    private readonly ToolStripMenuItem _pauseItem;

    public TrayIcon(AppModel model, Action showSettings, Action quit)
    {
        _model = model;

        _pauseItem = new ToolStripMenuItem("Pause");
        _pauseItem.Click += (_, _) =>
        {
            _model.IsPaused = !_model.IsPaused;
            _pauseItem.Text = _model.IsPaused ? "Resume" : "Pause";
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => showSettings());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit BendWin", null, (_, _) => quit());

        _notify = new NotifyIcon
        {
            Text    = "BendWin",
            Visible = true,
            Icon    = LoadIcon(),
            ContextMenuStrip = menu,
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) showSettings();
        };
    }

    private static Icon LoadIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "icon.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { }
        return SystemIcons.Application;
    }

    public void UpdateSensorStatus(string text) =>
        _notify.Text = $"BendWin — {text}"[..Math.Min(63, $"BendWin — {text}".Length)];

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
    }
}
