// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Grabs a picture of whatever is on screen right now (all monitors combined) and hands
//   back JPEG bytes. This only works when it's called from inside an interactive desktop
//   session — the Windows service itself runs in session 0, which has no desktop to
//   capture, which is why capture only ever happens via the "capture-screenshot <id>"
//   verb scheduled into the logged-on user's session (see ScheduledTaskService). The
//   image is downscaled and JPEG-compressed so it stays small enough to commit to GitHub
//   without bloating the control repo.
// =====================================================================================

using System.Drawing;                 // Bitmap / Graphics
using System.Drawing.Drawing2D;       // InterpolationMode
using System.Drawing.Imaging;         // ImageCodecInfo / EncoderParameters (JPEG quality)
using System.Runtime.InteropServices; // GetSystemMetrics P/Invoke
using System.Runtime.Versioning;      // [SupportedOSPlatform]

namespace Orchestrator.Service.Services;

public interface IScreenCaptureService
{
    /// <summary>Capture the full virtual screen (all monitors) as JPEG bytes. Null on failure
    /// (no desktop available, capture blocked, etc.) — this is always best-effort.</summary>
    byte[]? CaptureJpeg();
}

[SupportedOSPlatform("windows")]
public sealed class ScreenCaptureService : IScreenCaptureService
{
    private const int MaxWidth = 1600;   // downscale above this so uploads/repo growth stay small
    private const long JpegQuality = 60L;

    // GetSystemMetrics indices for the virtual screen (spans all monitors, handles negative
    // origins when a monitor is positioned left of/above the primary one).
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public byte[]? CaptureJpeg()
    {
        try
        {
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (width <= 0 || height <= 0) return null;   // no desktop to capture (e.g. locked/no session)

            using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

            using var scaled = Downscale(full, MaxWidth);
            return EncodeJpeg(scaled, JpegQuality);
        }
        catch
        {
            return null;   // best-effort: a locked workstation, RDP disconnect, etc. shouldn't crash the caller
        }
    }

    private static Bitmap Downscale(Bitmap src, int maxWidth)
    {
        if (src.Width <= maxWidth) return new Bitmap(src);
        var ratio = (double)maxWidth / src.Width;
        var w = maxWidth;
        var h = Math.Max(1, (int)(src.Height * ratio));
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    private static byte[] EncodeJpeg(Bitmap bmp, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var eps = new EncoderParameters(1);
        eps.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, encoder, eps);
        return ms.ToArray();
    }
}
