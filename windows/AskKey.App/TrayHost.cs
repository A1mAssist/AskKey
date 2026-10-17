using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AskKeyApp;

public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _pending;
    private readonly ToolStripMenuItem _pause;
    private readonly ToolStripMenuItem _settings;
    private readonly ToolStripMenuItem _quit;
    private readonly Action _showMain;
    private readonly Action _showPending;
    private readonly Action _showSettings;
    private readonly Action _toggleAgentAccess;
    private readonly Action _quitAction;

    public TrayHost(
        Action showMain,
        Action showPending,
        Action showSettings,
        Action toggleAgentAccess,
        Action quit)
    {
        _showMain = showMain;
        _showPending = showPending;
        _showSettings = showSettings;
        _toggleAgentAccess = toggleAgentAccess;
        _quitAction = quit;

        _open = new ToolStripMenuItem(Loc.T("Open Ask Key"), null, (_, _) => _showMain()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _pending = new ToolStripMenuItem(Loc.T("Pending requests"), null, (_, _) => _showPending());
        _pause = new ToolStripMenuItem(Loc.T("Pause Agent access"), null, (_, _) => _toggleAgentAccess());
        _settings = new ToolStripMenuItem(Loc.T("Settings"), null, (_, _) => _showSettings());
        _quit = new ToolStripMenuItem(Loc.T("Quit Ask Key"), null, (_, _) => _quitAction());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_open);
        menu.Items.Add(_pending);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(_settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_quit);

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = Loc.T("Ask Key"),
            Visible = true,
            ContextMenuStrip = menu,
        };
        ApplyLanguage();
        _icon.DoubleClick += (_, _) => _showMain();
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _notificationClick;
            _notificationClick = null;
            (action ?? _showPending)();
        };
    }

    private int _pendingCount;
    private bool _paused;
    private Action? _notificationClick;

    public void UpdatePendingCount(int count)
    {
        _pendingCount = count;
        _pending.Text = count > 0 ? Loc.F("Pending requests ({0})", count) : Loc.T("Pending requests");
        _pending.Enabled = count > 0;
        UpdateIconText();
    }

    public void UpdatePaused(bool paused)
    {
        _paused = paused;
        _pause.Text = paused ? Loc.T("Resume Agent access") : Loc.T("Pause Agent access");
        UpdateIconText();
    }

    private void UpdateIconText()
    {
        var status = _pendingCount > 0 ? Loc.F(" · {0} waiting", _pendingCount) : "";
        if (_paused) status += Loc.T(" · paused");
        _icon.Text = Loc.T("Ask Key") + status;
    }

    public void ApplyLanguage()
    {
        _open.Text = Loc.T("Open Ask Key");
        _settings.Text = Loc.T("Settings");
        _quit.Text = Loc.T("Quit Ask Key");
        UpdatePendingCount(_pendingCount);
        UpdatePaused(_paused);
    }

    public void ShowNotification(string title, string text, Action? onClick = null)
    {
        _notificationClick = onClick;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

public static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var path = RoundedRect(new RectangleF(1, 1, 30, 30), 8);
            using var background = new SolidBrush(Color.FromArgb(46, 107, 255));
            graphics.FillPath(background, path);
            using var font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("K", font, text, new RectangleF(0, 0, 32, 33), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
