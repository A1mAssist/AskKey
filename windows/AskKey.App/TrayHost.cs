using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AskKeyApp;

public sealed class TrayHost : IDisposable
{
    private static readonly Font MenuFont = new("Segoe UI Variable Text", 9f);
    private static readonly Font MenuBoldFont = new(MenuFont, FontStyle.Bold);
    private readonly ContextMenuStrip _menu;
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

        _open = new ToolStripMenuItem(Loc.T("Open Ask Key"), null, (_, _) => _showMain()) { Font = MenuBoldFont };
        _pending = new ToolStripMenuItem(Loc.T("Pending requests"), null, (_, _) => _showPending());
        _pause = new ToolStripMenuItem(Loc.T("Pause Agent access"), null, (_, _) => _toggleAgentAccess());
        _settings = new ToolStripMenuItem(Loc.T("Settings"), null, (_, _) => _showSettings());
        _quit = new ToolStripMenuItem(Loc.T("Quit Ask Key"), null, (_, _) => _quitAction());

        _menu = new ContextMenuStrip
        {
            Font = MenuFont,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(6),
            MinimumSize = new Size(232, 0),
        };
        _menu.Items.Add(_open);
        _menu.Items.Add(_pending);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_pause);
        _menu.Items.Add(_settings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_quit);
        _menu.Opening += (_, _) => ApplyTheme(AppearanceManager.IsDark());
        _menu.Resize += (_, _) => ApplyRoundedCorners();

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = Loc.T("Ask Key"),
            Visible = true,
            ContextMenuStrip = _menu,
        };
        ApplyTheme(AppearanceManager.IsDark());
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
    private Color _menuText = Color.Empty;
    private Color _menuTertiary = Color.Empty;

    public void UpdatePendingCount(int count)
    {
        _pendingCount = count;
        _pending.Text = count > 0 ? Loc.F("Pending requests ({0})", count) : Loc.T("Pending requests");
        _pending.Enabled = count > 0;
        _pending.ForeColor = _pending.Enabled ? _menuText : _menuTertiary;
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

    public void ApplyTheme(bool dark)
    {
        var surface = dark ? Color.FromArgb(255, 35, 38, 42) : Color.White;
        var text = dark ? Color.FromArgb(255, 242, 243, 245) : Color.FromArgb(255, 28, 31, 35);
        var tertiary = dark ? Color.FromArgb(255, 129, 136, 144) : Color.FromArgb(255, 142, 148, 154);
        var border = dark ? Color.FromArgb(255, 58, 63, 69) : Color.FromArgb(255, 225, 227, 223);
        var hover = dark ? Color.FromArgb(255, 48, 53, 59) : Color.FromArgb(255, 241, 242, 239);

        _menuText = text;
        _menuTertiary = tertiary;
        _menu.BackColor = surface;
        _menu.ForeColor = text;
        _menu.Renderer = new TrayMenuRenderer(surface, border, hover);
        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = text;
            item.Font = ReferenceEquals(item, _open) ? MenuBoldFont : MenuFont;
            if (item is not ToolStripSeparator) item.Padding = new Padding(8, 5, 8, 5);
        }
        _pending.ForeColor = _pending.Enabled ? text : tertiary;
        ApplyRoundedCorners();
        _menu.Refresh();
    }

    public void ApplyLanguage()
    {
        _open.Text = Loc.T("Open Ask Key");
        _settings.Text = Loc.T("Settings");
        _quit.Text = Loc.T("Quit Ask Key");
        UpdatePendingCount(_pendingCount);
        UpdatePaused(_paused);
    }

    private void ApplyRoundedCorners()
    {
        if (_menu.Width <= 0 || _menu.Height <= 0) return;
        _menu.Region?.Dispose();
        using var path = RoundedRect(new Rectangle(0, 0, _menu.Width, _menu.Height), 8);
        _menu.Region = new Region(path);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, float radius)
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
        _menu.Region?.Dispose();
        _menu.Dispose();
    }

    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Color _surface;
        private readonly Color _border;
        private readonly Color _hover;

        public TrayMenuRenderer(Color surface, Color border, Color hover)
        {
            _surface = surface;
            _border = border;
            _hover = hover;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_surface);
            e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            using var brush = new SolidBrush(_hover);
            e.Graphics.FillRectangle(brush, new Rectangle(2, 1, Math.Max(0, e.Item.Width - 4),
                Math.Max(0, e.Item.Height - 2)));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.ForeColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var bounds = e.Item.ContentRectangle;
            var y = bounds.Top + bounds.Height / 2;
            using var pen = new Pen(_border);
            e.Graphics.DrawLine(pen, bounds.Left + 4, y, bounds.Right - 4, y);
        }
    }
}

public static class AppIcon
{
    public static Icon Create()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "Assets", "AskKey.ico");
        if (File.Exists(packaged)) return new Icon(packaged);

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            using var associated = Icon.ExtractAssociatedIcon(executable);
            if (associated is not null) return (Icon)associated.Clone();
        }
        return (Icon)SystemIcons.Application.Clone();
    }
}
