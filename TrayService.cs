using System.Drawing;
using Forms = System.Windows.Forms;

namespace Port;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _iconImage;

    public TrayService(Action toggle)
    {
        _iconImage = PortIcon.CreateIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _iconImage,
            Visible = true,
            Text = "port"
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                toggle();
        };
    }

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
}
