using System.Drawing;
using System.Windows.Forms;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// Windows notification-area icon and its functional context menu.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _openItem;
    private Icon? _icon;
    private bool _disposed;

    public TrayService(
        Action openSearch,
        Action openSettings,
        Func<Task> checkUpdates,
        Action exit)
    {
        _icon = CreateIcon();

        _openItem = new ToolStripMenuItem("Открыть поиск", null, (_, _) => openSearch())
        {
            Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold)
        };

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f),
            Renderer = new DarkMenuRenderer()
        };

        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripMenuItem("Настройки", null, (_, _) => openSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Проверить обновления", null, async (_, _) => await checkUpdates()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => exit()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Universal Search Launcher",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                openSearch();
            }
        };
        _notifyIcon.DoubleClick += (_, _) => openSearch();
    }

    public void SetHotkeyText(string display)
    {
        _openItem.Text = $"Открыть поиск ({display})";
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(5000);
        }
        catch
        {
            // Notification area can be unavailable during shell shutdown.
        }
    }

    private static Icon CreateIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Fall through to a system icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();
        _icon = null;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColorTable())
        {
            RoundedEdges = true;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(68, 72, 86));
            var rectangle = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, rectangle);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
            {
                e.TextColor = Color.FromArgb(110, 116, 133);
            }
            else
            {
                e.TextColor = e.Item.Selected
                    ? Color.FromArgb(255, 255, 255)
                    : Color.FromArgb(232, 235, 244);
            }

            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(30, 32, 40);
        private static readonly Color Border = Color.FromArgb(68, 72, 86);
        private static readonly Color Selected = Color.FromArgb(55, 95, 135);
        private static readonly Color Text = Color.FromArgb(238, 240, 245);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Selected;
        public override Color MenuItemSelected => Selected;
        public override Color MenuItemSelectedGradientBegin => Selected;
        public override Color MenuItemSelectedGradientEnd => Selected;
        public override Color MenuItemPressedGradientBegin => Selected;
        public override Color MenuItemPressedGradientEnd => Selected;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color ToolStripBorder => Border;
    }
}
