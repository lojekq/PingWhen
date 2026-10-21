namespace PingWhen.Features;

/// <summary>
/// Region frames (v1.0, spec §5.4): the region is cut from the window frame, scaled to 64 px wide in grayscale,
/// and frames are compared by mean absolute difference. Pure, no Windows.
/// </summary>
public static class Frames
{
    public const int Width = 64;
    public const double DefaultThreshold = 2.0; // % of full scale

    /// <summary>Region as fractions of the window frame, clamped; it survives moves and resizes.</summary>
    public readonly record struct Rect(double X, double Y, double W, double H)
    {
        public Rect Clamp()
        {
            var x = Math.Clamp(X, 0, 1); var y = Math.Clamp(Y, 0, 1);
            return new(x, y, Math.Clamp(W, 0, 1 - x), Math.Clamp(H, 0, 1 - y));
        }
        public bool IsEmpty => W <= 0.001 || H <= 0.001;
        public (int X, int Y, int W, int H) Pixels(int width, int height)
        {
            var c = Clamp();
            int x = (int)(c.X * width), y = (int)(c.Y * height);
            return (x, y, Math.Max(1, Math.Min(width - x, (int)Math.Round(c.W * width))), Math.Max(1, Math.Min(height - y, (int)Math.Round(c.H * height))));
        }
    }

    /// <summary>BGRA (stride = width * 4) → grayscale 64 px wide, box-averaged. Returns the gray pixels and their height.</summary>
    public static (byte[] Gray, int Height) Gray64(ReadOnlySpan<byte> bgra, int width, int height, Rect region)
    {
        var (rx, ry, rw, rh) = region.Pixels(width, height);
        var gh = Math.Max(1, (int)Math.Round(rh * (double)Width / rw));
        var gray = new byte[Width * gh];
        for (var gy = 0; gy < gh; gy++)
        {
            int y0 = ry + gy * rh / gh, y1 = Math.Max(y0 + 1, ry + (gy + 1) * rh / gh);
            for (var gx = 0; gx < Width; gx++)
            {
                int x0 = rx + gx * rw / Width, x1 = Math.Max(x0 + 1, rx + (gx + 1) * rw / Width);
                long sum = 0; var n = 0;
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        var i = (y * width + x) * 4;
                        sum += (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8; // BT.601 luma
                        n++;
                    }
                gray[gy * Width + gx] = (byte)(sum / n);
            }
        }
        return (gray, gh);
    }

    /// <summary>Mean absolute difference in % (0–100). Different sizes (window resized) count as a full change.</summary>
    public static double Diff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 100;
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum * 100.0 / (a.Length * 255.0);
    }

    /// <summary>DRM or a GPU surface we can't read gives an all-black frame.</summary>
    public static bool IsBlack(byte[] gray) => gray.All(g => g < 8);
}
