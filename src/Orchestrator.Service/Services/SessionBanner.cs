// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The mandatory on-screen indicator for a live remote-control session — a small,
//   always-on-top, unmissable banner. It is NOT optional and there is no config flag to hide
//   it: whoever is physically at the machine must always be able to see a session is active
//   and end it themselves (click anywhere on the banner), regardless of what the console side
//   wants. Built directly on the Win32 API (raw user32/gdi32 P/Invoke, same style as
//   ScreenCaptureService's GetSystemMetrics use) rather than WinForms/WPF, specifically so
//   this project keeps building and unit-testing on macOS/Linux — pulling in a UI framework
//   (UseWindowsForms) makes the WHOLE assembly require the Windows Desktop runtime at load
//   time, which would break `dotnet test` off Windows for every test, not just ones that
//   touch this file. This file's actual window-creation and message-loop calls are still
//   Windows-only at runtime, same as everything else behind [SupportedOSPlatform("windows")].
// =====================================================================================

using System.Runtime.InteropServices;   // P/Invoke
using System.Runtime.Versioning;        // [SupportedOSPlatform]

namespace Orchestrator.Service.Services;

[SupportedOSPlatform("windows")]
internal sealed class SessionBanner : IDisposable
{
    private const string ClassName = "OrchSessionBanner";
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_APP_END = 0x8000 + 1;   // custom: "close this from another thread"
    private const int SW_SHOW = 5;
    private const uint DT_CENTER = 0x0001;
    private const uint DT_VCENTER = 0x0004;
    private const uint DT_SINGLELINE = 0x0020;
    private const int TRANSPARENT_BKMODE = 1;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;   // no taskbar/alt-tab entry
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const int Width = 320;
    private const int Height = 64;
    private const string Text = "Remote control active — click to end";

    private readonly Action _onEnd;
    private readonly WndProcDelegate _wndProc;   // keep alive: native code holds a pointer to this
    private readonly ManualResetEventSlim _closed = new(false);
    private IntPtr _hwnd;

    public SessionBanner(Action onEnd)
    {
        _onEnd = onEnd;
        _wndProc = WndProc;   // capture the delegate instance once, not per-call
    }

    /// <summary>Create and show the window, then pump this thread's Win32 message loop until
    /// the window closes. Call this on its own dedicated thread — it blocks until closed.</summary>
    public void RunMessageLoop()
    {
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hbrBackground = CreateSolidBrush(0x002626DC),   // COLORREF 0x00BBGGRR -> a red banner
            lpszClassName = ClassName
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassExW failed (error {Marshal.GetLastWin32Error()})");

        var screenW = GetSystemMetrics(SM_CXSCREEN);
        var x = Math.Max(0, screenW - Width - 16);
        const int y = 16;

        _hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName, "Orchestrator",
            WS_POPUP | WS_VISIBLE, x, y, Width, Height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _closed.Set();
            throw new InvalidOperationException($"CreateWindowExW failed (error {Marshal.GetLastWin32Error()})");
        }

        ShowWindow(_hwnd, SW_SHOW);
        UpdateWindow(_hwnd);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        _closed.Set();
    }

    /// <summary>Ask the banner to close from another thread. Safe to call multiple times, and
    /// safe to call before the window exists yet (a no-op in that case).</summary>
    public void RequestClose()
    {
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_APP_END, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Wait (bounded) for the message loop to actually finish. Returns false on timeout.</summary>
    public bool WaitForClose(TimeSpan timeout) => _closed.Wait(timeout);

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint(hWnd);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
            case WM_CLOSE:
            case WM_APP_END:
                // Any of these — a click, the OS asking us to close, or a cross-thread request —
                // ends the session. This is the person-at-the-keyboard's override and it always wins.
                try { _onEnd(); } catch { /* session may already be ending */ }
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private static void Paint(IntPtr hWnd)
    {
        var hdc = BeginPaint(hWnd, out var ps);
        GetClientRect(hWnd, out var rect);
        SetBkMode(hdc, TRANSPARENT_BKMODE);
        SetTextColor(hdc, 0x00FFFFFF);   // white
        DrawTextW(hdc, Text, Text.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        EndPaint(hWnd, ref ps);
    }

    public void Dispose() => _closed.Dispose();

    // ---- Win32 P/Invoke surface ---------------------------------------------------------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        [MarshalAs(UnmanagedType.Bool)] public bool fErase;
        public RECT rcPaint;
        [MarshalAs(UnmanagedType.Bool)] public bool fRestore;
        [MarshalAs(UnmanagedType.Bool)] public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int crColor);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int crColor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpString, int nCount, ref RECT lpRect, uint uFormat);
}
