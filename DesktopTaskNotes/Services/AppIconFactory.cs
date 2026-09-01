using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Imaging = System.Windows.Interop.Imaging;
using ImageSource = System.Windows.Media.ImageSource;

namespace DesktopTaskNotes.Services;

internal static class AppIconFactory
{
    public static Icon CreateTrayIcon()
    {
        using var bitmap = CreateBitmap(32);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static ImageSource CreateImageSource()
    {
        using var icon = CreateTrayIcon();
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
        source.Freeze();
        return source;
    }

    private static Bitmap CreateBitmap(int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var path = RoundedRectangle(new RectangleF(1, 1, size - 2, size - 2), 8);
        using var fill = new SolidBrush(Color.FromArgb(91, 91, 214));
        graphics.FillPath(fill, path);
        using var pen = new Pen(Color.White, 3.1f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(pen,
        [
            new PointF(size * 0.27f, size * 0.52f),
            new PointF(size * 0.43f, size * 0.68f),
            new PointF(size * 0.73f, size * 0.34f)
        ]);
        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
