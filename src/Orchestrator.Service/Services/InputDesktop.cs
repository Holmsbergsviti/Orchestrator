// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Windows shows different things on different "desktops" inside one sign-in session: the
//   normal one (Default), and a separate protected one (Winlogon) used for the login screen,
//   the lock screen, Ctrl+Alt+Del and the "Do you want to allow this app to make changes?"
//   (UAC) prompt. A process only sees and controls the desktop its thread is attached to, so a
//   remote session that stayed on Default would show a frozen picture and ignore the keyboard
//   whenever one of those screens is up.
//
//   This lets a thread attach itself to whichever desktop is currently being shown (the "input
//   desktop"). Capture and input call it before each frame / each key press. Only a SYSTEM
//   process may open the protected desktop, which is why the helper is started from the service.
//   A thread that owns windows cannot be moved, so the banner has its own, separate handling.
// =====================================================================================

using System.Runtime.InteropServices;   // P/Invoke
using System.Runtime.Versioning;        // [SupportedOSPlatform]
using System.Text;                      // StringBuilder for the desktop's name

namespace Orchestrator.Service.Services;

[SupportedOSPlatform("windows")]
public static class InputDesktop
{
    private const uint GENERIC_ALL = 0x10000000;
    private const int UOI_NAME = 2;

    [ThreadStatic] private static IntPtr _held;   // the desktop handle this thread switched to (kept open while it is in use)

    /// <summary>Make the calling thread use the desktop that is on screen right now. True when it is
    /// attached (already was, or was switched); false when that could not be done (for example the
    /// desktop is changing at this very moment) - callers then simply try again on the next frame.</summary>
    public static bool Attach()
    {
        var input = OpenInputDesktop(0, false, GENERIC_ALL);
        if (input == IntPtr.Zero) return false;   // briefly none while Windows switches desktops
        try
        {
            if (string.Equals(NameOf(input), NameOf(GetThreadDesktop(GetCurrentThreadId())), StringComparison.OrdinalIgnoreCase))
            {
                CloseDesktop(input);
                return true;   // already on it
            }
            if (!SetThreadDesktop(input))
            {
                CloseDesktop(input);
                return false;   // e.g. this thread owns a window or hook
            }
            var previous = _held;
            _held = input;                                            // stays open: the thread is using it now
            if (previous != IntPtr.Zero) CloseDesktop(previous);      // the one we left (never the process's own, which we didn't open)
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The name of the desktop on screen right now ("Default", "Winlogon"), or null if it can't be read.</summary>
    public static string? CurrentInputName()
    {
        var input = OpenInputDesktop(0, false, GENERIC_ALL);
        if (input == IntPtr.Zero) return null;
        try { return NameOf(input); }
        finally { CloseDesktop(input); }
    }

    /// <summary>The name of the desktop the calling thread is attached to.</summary>
    public static string? ThreadDesktopName() => NameOf(GetThreadDesktop(GetCurrentThreadId()));

    private static string? NameOf(IntPtr desktop)
    {
        if (desktop == IntPtr.Zero) return null;
        var sb = new StringBuilder(256);
        return GetUserObjectInformationW(desktop, UOI_NAME, sb, sb.Capacity * 2, out _) ? sb.ToString() : null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr obj, int index, StringBuilder info, int length, out int needed);
}
