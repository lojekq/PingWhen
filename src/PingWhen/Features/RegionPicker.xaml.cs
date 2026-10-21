using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PingWhen.Shell;

namespace PingWhen.Features;

/// <summary>The region is drawn with the mouse on a still frame of the window (spec §7); stored as fractions of the frame.</summary>
public partial class RegionPicker
{
    Point? start;
    Frames.Rect result = new(0, 0, 1, 1);

    RegionPicker(BitmapSource frame, Frames.Rect current)
    {
        InitializeComponent();
        Shot.Source = frame;
        Overlay.Width = frame.PixelWidth;
        Overlay.Height = frame.PixelHeight;
        if (current is { IsEmpty: false } c && c != new Frames.Rect(0, 0, 1, 1)) ShowBox(c);
        Overlay.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(Overlay); Overlay.CaptureMouse(); };
        Overlay.MouseMove += (_, e) => { if (start is { } s) Draw(s, e.GetPosition(Overlay)); };
        Overlay.MouseLeftButtonUp += (_, e) =>
        {
            Overlay.ReleaseMouseCapture();
            if (start is { } s) Draw(s, e.GetPosition(Overlay));
            start = null;
        };
        OkButton.Click += (_, _) => { DialogResult = true; };
        WholeButton.Click += (_, _) => { result = new(0, 0, 1, 1); DialogResult = true; };
    }

    void Draw(Point a, Point b)
    {
        double w = Overlay.Width, h = Overlay.Height;
        var x0 = Math.Clamp(Math.Min(a.X, b.X), 0, w); var y0 = Math.Clamp(Math.Min(a.Y, b.Y), 0, h);
        var x1 = Math.Clamp(Math.Max(a.X, b.X), 0, w); var y1 = Math.Clamp(Math.Max(a.Y, b.Y), 0, h);
        if (x1 - x0 < 4 || y1 - y0 < 4) return;
        ShowBox(new Frames.Rect(x0 / w, y0 / h, (x1 - x0) / w, (y1 - y0) / h));
    }

    void ShowBox(Frames.Rect r)
    {
        result = r;
        Canvas.SetLeft(Box, r.X * Overlay.Width);
        Canvas.SetTop(Box, r.Y * Overlay.Height);
        Box.Width = r.W * Overlay.Width;
        Box.Height = r.H * Overlay.Height;
        Box.Visibility = Visibility.Visible;
    }

    /// <summary>Null when cancelled or the window can't be captured (minimized: Windows gives no frames).</summary>
    public static async Task<Frames.Rect?> Pick(Window owner, long hwnd, Frames.Rect current)
    {
        if (await RegionCapture.Snapshot(hwnd, TimeSpan.FromSeconds(3)) is not { } s)
        {
            MessageBox.Show(owner, Loc.T("Region.NoFrame"), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var frame = BitmapSource.Create(s.Width, s.Height, 96, 96, PixelFormats.Bgra32, null, s.Bgra, s.Width * 4);
        var picker = new RegionPicker(frame, current) { Owner = owner };
        Theme.Apply(picker, Settings.Current.Theme);
        return picker.ShowDialog() == true ? picker.result.Clamp() : null;
    }
}
