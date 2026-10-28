// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Grabs a picture of whatever is on screen right now (all monitors combined) and hands
//   back JPEG bytes. This only works when it's called from inside an interactive desktop
//   session — the Windows service itself runs in session 0, which has no desktop to
//   capture, which is why capture only ever happens via the "capture-screenshot <id>"
//   verb scheduled into the logged-on user's session (see ScheduledTaskService). The
//   image is downscaled and JPEG-compressed so it stays small enough to commit to GitHub
//   without bloating the control repo.
//
//   It has two entry points. CaptureJpeg is the one-shot screenshot: quality over speed.
//   CaptureFrame is the live remote-control stream, called many times a second, so it
//   reuses one frame buffer and lets GDI capture and shrink the screen in a single
//   StretchBlt instead of copying the full-resolution desktop and then resampling it with
//   bicubic filtering — that two-step path alone costs more than a frame's whole budget
//   on a 4K or multi-monitor desktop, which is what held the stream to ~2 fps.
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

    /// <summary>Capture the same view as <see cref="CaptureJpeg"/>, but built for a live stream:
    /// fast enough to call 10+ times a second, at slightly lower fidelity. Null on failure.</summary>
    byte[]? CaptureFrame();
}

[SupportedOSPlatform("windows")]
public sealed class ScreenCaptureService : IScreenCaptureService
{
    private const int MaxWidth = 1600;   // downscale above this so uploads/repo growth stay small
    private const long JpegQuality = 60L;
    private const long FrameJpegQuality = 50L;   // live frames: smaller beats sharper — the next one is ~70 ms away

    // The JPEG encoder never changes, so look it up once rather than on every frame.
    private static readonly ImageCodecInfo JpegEncoder =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // The live stream's reusable frame buffer. Allocating a fresh multi-megabyte bitmap per
    // frame is pure GC churn; it's only rebuilt when the desktop's size actually changes. The
    // lock keeps two callers from drawing into it at once (in practice there's only ever one:
    // each remote session is its own process).
    private readonly object _frameLock = new();
    private Bitmap? _frame;

    // GetSystemMetrics indices for the virtual screen (spans all monitors, handles negative
    // origins when a monitor is positioned left of/above the primary one).
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // Raw GDI for the live-frame path: grab the screen's device context and stretch-copy it
    // straight into our (smaller) frame buffer in one call.
    private const int SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;   // averages source pixels when shrinking, so text stays legible

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

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

    public byte[]? CaptureFrame()
    {
        lock (_frameLock)
        {
            try
            {
                var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
                var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
                var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (width <= 0 || height <= 0) return null;   // no desktop to capture (e.g. locked/no session)

                // Same downscale rule as the screenshot, so the viewer's picture (and the input
                // mapping, which works in fractions of it) doesn't change shape between the two.
                var (w, h) = ScaledSize(width, height, MaxWidth);
                if (_frame is null || _frame.Width != w || _frame.Height != h)
                {
                    _frame?.Dispose();   // monitor plugged in/out or resolution changed mid-session
                    _frame = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                }

                using (var g = Graphics.FromImage(_frame))
                {
                    var dest = g.GetHdc();
                    var screen = GetDC(IntPtr.Zero);
                    try
                    {
                        SetStretchBltMode(dest, HALFTONE);
                        SetBrushOrgEx(dest, 0, 0, IntPtr.Zero);   // required after switching to HALFTONE
                        if (!StretchBlt(dest, 0, 0, w, h, screen, x, y, width, height, SRCCOPY))
                            return null;   // the desktop went away between the size check and the copy
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, screen);
                        g.ReleaseHdc(dest);   // this is also what flushes the copied pixels into _frame
                    }
                }

                return EncodeJpeg(_frame, FrameJpegQuality);
            }
            catch
            {
                return null;   // best-effort, same as CaptureJpeg: one bad frame must not end the session
            }
        }
    }

    /// <summary>Fit a desktop of the given size within <paramref name="maxWidth"/>, keeping its shape.</summary>
    private static (int Width, int Height) ScaledSize(int width, int height, int maxWidth)
        => width <= maxWidth ? (width, height) : (maxWidth, Math.Max(1, (int)(height * ((double)maxWidth / width))));

    private static Bitmap Downscale(Bitmap src, int maxWidth)
    {
        if (src.Width <= maxWidth) return new Bitmap(src);
        var (w, h) = ScaledSize(src.Width, src.Height, maxWidth);
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    private static byte[] EncodeJpeg(Bitmap bmp, long quality)
    {
        using var eps = new EncoderParameters(1);
        eps.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, JpegEncoder, eps);
        return ms.ToArray();
    }
}
