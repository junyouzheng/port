using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using DrawingColor = System.Drawing.Color;

namespace Port;

internal static class PortIcon
{
    public static Icon CreateIcon()
    {
        using var bmp = Draw(32);
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

    public static BitmapSource CreateImage()
    {
        using var bmp = Draw(32);
        using var stream = new MemoryStream();
        bmp.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(DrawingColor.Transparent);
        using var gold = new SolidBrush(DrawingColor.FromArgb(243, 197, 106));
        g.FillEllipse(gold, 1, 1, size - 2, size - 2);
        using var cream = new SolidBrush(DrawingColor.FromArgb(255, 248, 236));
        var inset = size * 9 / 32;
        var inner = size * 14 / 32;
        g.FillEllipse(cream, inset, inset, inner, inner);
        return bmp;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
