using System.Net;
using System.Net.Http;
using System.Text;
using PingWhen.Features;
using static PingWhen.Features.Conditions;

namespace PingWhen.Tests;

/// <summary>v1.0 stage 7: frame comparison, region conditions, photos.</summary>
[TestClass]
public class RegionTests
{
    /// <summary>width × height BGRA filled with one gray level, plus an optional brighter block.</summary>
    static byte[] Image(int w, int h, byte level, (int X, int Y, int W, int H, byte Level)? block = null, int noise = 0, int seed = 1)
    {
        var rng = new Random(seed);
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = block is { } b && x >= b.X && x < b.X + b.W && y >= b.Y && y < b.Y + b.H ? b.Level : level;
                var n = noise == 0 ? v : Math.Clamp(v + rng.Next(-noise, noise + 1), 0, 255);
                var i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = (byte)n;
                px[i + 3] = 255;
            }
        return px;
    }

    static readonly Frames.Rect Whole = new(0, 0, 1, 1);

    [TestMethod]
    public void GrayIs64WideAndKeepsTheAspect()
    {
        var (gray, h) = Frames.Gray64(Image(640, 160, 100), 640, 160, Whole);
        Assert.AreEqual(16, h);
        Assert.HasCount(64 * 16, gray);
        Assert.IsTrue(gray.All(g => Math.Abs(g - 100) <= 1));
    }

    [TestMethod]
    public void RegionCutsTheRightPart()
    {
        var img = Image(400, 200, 0, (200, 100, 200, 100, 200)); // bright bottom-right quarter
        var (bright, _) = Frames.Gray64(img, 400, 200, new(0.5, 0.5, 0.5, 0.5));
        var (dark, _) = Frames.Gray64(img, 400, 200, new(0, 0, 0.5, 0.5));
        Assert.IsTrue(bright.All(g => g > 190));
        Assert.IsTrue(Frames.IsBlack(dark));
    }

    [TestMethod]
    public void SameFramesAreZeroNoiseIsBelowAndAChangeIsAboveTheThreshold()
    {
        var a = Frames.Gray64(Image(320, 120, 120), 320, 120, Whole).Gray;
        var noisy = Frames.Gray64(Image(320, 120, 120, noise: 6, seed: 7), 320, 120, Whole).Gray;
        var bar = Frames.Gray64(Image(320, 120, 120, (0, 40, 160, 40, 250)), 320, 120, Whole).Gray; // a progress bar half full
        Assert.AreEqual(0, Frames.Diff(a, a));
        Assert.IsLessThan(Frames.DefaultThreshold, Frames.Diff(a, noisy));
        Assert.IsGreaterThan(Frames.DefaultThreshold, Frames.Diff(a, bar));
        Assert.AreEqual(100, Frames.Diff(a, a[..^64])); // resized window
    }

    [TestMethod]
    public void UnchangedFiresOnlyAfterAChangeAndNSeconds()
    {
        var a = new byte[64]; var b = Enumerable.Repeat((byte)200, 64).ToArray();
        var s = new RegionState();
        (s, _) = RegionFrame(s, a, 2, 1000);                       // baseline
        Assert.IsFalse(RegionUnchanged(s, 5, 60_000), "static region alone never fires");
        (s, var changed) = RegionFrame(s, b, 2, 2000);
        Assert.IsTrue(changed);
        Assert.IsFalse(RegionUnchanged(s, 5, 6000));
        Assert.IsTrue(RegionUnchanged(s, 5, 7000));
        Assert.IsTrue(RegionChanged(s, b, 2));
        Assert.IsFalse(RegionChanged(s, a, 2));
    }

    [TestMethod]
    public void JpegFitsIn300KbEvenForALargeNoisyWindow()
    {
        var img = Image(2560, 1440, 128, noise: 120);
        var jpeg = RegionCapture.Jpeg(img, 2560, 1440, null);
        Assert.IsLessThanOrEqualTo(300_000, jpeg.Length);
        Assert.AreEqual(0xFF, jpeg[0]);
        Assert.AreEqual(0xD8, jpeg[1]);
        Assert.IsLessThan(jpeg.Length, RegionCapture.Jpeg(img, 2560, 1440, new(0, 0, 0.25, 0.25)).Length + 1);
    }

    /// <summary>Decision 18: a watcher's screenshot tick alone never sends the picture off the PC.</summary>
    [TestMethod]
    public void ScreenshotsStayOnThePcUnlessScreenshotsToThePhoneIsOn()
    {
        Assert.IsFalse(new Watcher().Screenshot);
        Assert.IsFalse(new PingWhen.Shell.Settings().PhoneScreenshots);
        var n = new Note("Done: timeout 30", "code 0", ImagePath: @"C:\Users\demo\shot.jpg");
        var saved = PingWhen.Shell.Settings.Current.PhoneScreenshots;
        try
        {
            PingWhen.Shell.Settings.Current.PhoneScreenshots = false;
            Assert.IsNull(Notifier.ForPhone(n).ImagePath, "default: no picture to Telegram or ntfy");
            Assert.AreEqual("Done: timeout 30", Notifier.ForPhone(n).Title);
            PingWhen.Shell.Settings.Current.PhoneScreenshots = true;
            Assert.AreEqual(n.ImagePath, Notifier.ForPhone(n).ImagePath);
        }
        finally { PingWhen.Shell.Settings.Current.PhoneScreenshots = saved; }
    }

    sealed class Stub : HttpMessageHandler
    {
        public string? Method, ContentType, Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            (Method, ContentType, Body) = (r.RequestUri!.Segments[^1], r.Content!.Headers.ContentType!.MediaType, await r.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"result":{}}""", Encoding.UTF8, "application/json") };
        }
    }

    [TestMethod]
    public async Task SendPhotoIsMultipartWithACaptionOfAtMost1024()
    {
        var stub = new Stub();
        await new Telegram(new HttpClient(stub), "123456:AAFakeFakeFakeFakeFakeFakeFakeFake_x").SendPhoto(555, [0xFF, 0xD8, 1, 2], new string('c', 1500), silent: true);
        Assert.AreEqual("sendPhoto", stub.Method);
        Assert.AreEqual("multipart/form-data", stub.ContentType);
        StringAssert.Contains(stub.Body, "name=chat_id");
        StringAssert.Contains(stub.Body, "555");
        StringAssert.Contains(stub.Body, "filename=pingwhen.jpg");
        Assert.Contains(new string('c', 1023) + "…", stub.Body);
        Assert.DoesNotContain(new string('c', 1024), stub.Body);
    }
}
