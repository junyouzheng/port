using System.Drawing;
using System.Runtime.InteropServices;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace Port;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Icon _iconImage;

    public TrayService(Action toggle, Action toggleStartup, Action exit)
    {
        _iconImage = MakeIcon();
        _startupItem = new Forms.ToolStripMenuItem("开机自启动");
        _startupItem.Click += (_, _) => toggleStartup();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = _iconImage,
            Visible = true,
            Text = "本机端口与进程面板",
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                toggle();
        };
    }

    public void SetStartupChecked(bool enabled) => _startupItem.Checked = enabled;

    public void SetTip(string text)
    {
        if (text.Length > 63)
            text = text[..63];
        if (_icon.Text != text)
            _icon.Text = text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _iconImage.Dispose();
    }

    private static Icon MakeIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(DrawingColor.Transparent);
        using var gold = new SolidBrush(DrawingColor.FromArgb(243, 197, 106));
        g.FillEllipse(gold, 1, 1, size - 2, size - 2);
        using var cream = new SolidBrush(DrawingColor.FromArgb(255, 248, 236));
        g.FillEllipse(cream, 9, 9, 14, 14);
        var handle = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
