// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Turns the operator's mouse and keyboard events into real input on this machine, using
//   Win32 SendInput (same raw P/Invoke style as ScreenCaptureService and SessionBanner). This
//   is the half of remote control that actually TOUCHES the machine, so two things matter as
//   much as the injection itself:
//
//   1. Positions arrive as fractions of the virtual desktop and are injected with
//      MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, whose coordinate space is that same
//      virtual desktop scaled to 0..65535. That lines up with what ScreenCaptureService
//      captures, so multi-monitor setups and monitors positioned left of/above the primary
//      one work without any per-monitor arithmetic here.
//   2. Everything pressed is remembered, so ReleaseAll() can let it go when the session ends.
//      Without that, a session ending mid-gesture (banner clicked, relay dropped, timeout)
//      would leave a key or mouse button physically stuck down on someone's desktop, and the
//      person sitting there would have no idea why their machine had gone haywire.
// =====================================================================================

using System.Runtime.InteropServices;   // P/Invoke
using System.Runtime.Versioning;        // [SupportedOSPlatform]
using Microsoft.Extensions.Logging;     // logging
using Orchestrator.Service.Models;      // RemoteInputEvent

namespace Orchestrator.Service.Services;

public interface IRemoteInputInjector
{
    /// <summary>Apply one operator input event to this machine.</summary>
    void Inject(RemoteInputEvent evt);

    /// <summary>Release every key and mouse button this injector is still holding down.</summary>
    void ReleaseAll();
}

[SupportedOSPlatform("windows")]
public sealed class RemoteInputInjector : IRemoteInputInjector
{
    private readonly ILogger<RemoteInputInjector> _log;
    private readonly HashSet<ushort> _heldKeys = new();       // virtual-key codes currently down
    private readonly HashSet<string> _heldButtons = new();    // "left"/"right"/"middle" currently down
    private readonly object _lock = new();                    // events arrive on the WS receive loop; ReleaseAll from teardown

    public RemoteInputInjector(ILogger<RemoteInputInjector> log) => _log = log;

    public void Inject(RemoteInputEvent evt)
    {
        if (!OperatingSystem.IsWindows()) return;

        switch (evt.Kind)
        {
            case RemoteInputKind.MouseMove:
                SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, evt.X, evt.Y, 0);
                break;

            case RemoteInputKind.MouseDown:
            case RemoteInputKind.MouseUp:
                InjectButton(evt);
                break;

            case RemoteInputKind.Wheel:
                // WHEEL_DELTA (120) is one notch of a physical wheel.
                SendMouse(MOUSEEVENTF_WHEEL, evt.X, evt.Y, evt.WheelDelta * 120);
                break;

            case RemoteInputKind.KeyDown:
            case RemoteInputKind.KeyUp:
                InjectKey(evt);
                break;
        }
    }

    private void InjectButton(RemoteInputEvent evt)
    {
        var down = evt.Kind == RemoteInputKind.MouseDown;
        var flag = evt.Button switch
        {
            "left" => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            "right" => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            "middle" => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0u
        };
        if (flag == 0) return;

        // Move first, in the same absolute space, so the click lands where the operator is
        // pointing even if no mousemove preceded it (a fast click, or a dropped move event).
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, evt.X, evt.Y, 0);
        SendMouse(flag, evt.X, evt.Y, 0);

        lock (_lock)
        {
            if (down) _heldButtons.Add(evt.Button);
            else _heldButtons.Remove(evt.Button);
        }
    }

    private void InjectKey(RemoteInputEvent evt)
    {
        if (!KeyMap.TryGetValue(evt.Code, out var mapped))
        {
            _log.LogDebug("remote input: no virtual-key mapping for '{Code}'", evt.Code);
            return;
        }

        var down = evt.Kind == RemoteInputKind.KeyDown;
        SendKey(mapped.Vk, mapped.Extended, down);

        lock (_lock)
        {
            if (down) _heldKeys.Add(mapped.Vk);
            else _heldKeys.Remove(mapped.Vk);
        }
    }

    public void ReleaseAll()
    {
        if (!OperatingSystem.IsWindows()) return;

        ushort[] keys;
        string[] buttons;
        lock (_lock)
        {
            keys = _heldKeys.ToArray();
            buttons = _heldButtons.ToArray();
            _heldKeys.Clear();
            _heldButtons.Clear();
        }
        if (keys.Length == 0 && buttons.Length == 0) return;

        foreach (var vk in keys)
            SendKey(vk, IsExtendedVk(vk), down: false);
        foreach (var b in buttons)
        {
            var flag = b switch
            {
                "left" => MOUSEEVENTF_LEFTUP,
                "right" => MOUSEEVENTF_RIGHTUP,
                "middle" => MOUSEEVENTF_MIDDLEUP,
                _ => 0u
            };
            if (flag != 0) SendMouse(flag, 0, 0, 0);
        }
        _log.LogInformation("remote input: released {Keys} key(s) and {Buttons} button(s) still held at session end",
            keys.Length, buttons.Length);
    }

    // ---- the actual SendInput calls -------------------------------------------------------

    private void SendMouse(uint flags, double normalizedX, double normalizedY, int mouseData)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    // The absolute space is 0..65535 across the virtual desktop, so a fraction
                    // maps straight onto it. 65535 (not 65536) is the maximum valid coordinate.
                    dx = (int)Math.Round(normalizedX * 65535d),
                    dy = (int)Math.Round(normalizedY * 65535d),
                    mouseData = unchecked((uint)mouseData),
                    dwFlags = flags
                }
            }
        };
        Send(input);
    }

    private void SendKey(ushort vk, bool extended, bool down)
    {
        var flags = 0u;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;

        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } }
        });
    }

    private void Send(INPUT input)
    {
        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent == 0)
            // Usually UIPI: a service-launched process can't inject into an elevated window, and
            // nothing can inject into the secure desktop (UAC prompt, Ctrl+Alt+Del, lock screen).
            _log.LogDebug("remote input: SendInput was blocked (win32 error {Error})", Marshal.GetLastWin32Error());
    }

    // ---- key mapping ----------------------------------------------------------------------

    private readonly record struct MappedKey(ushort Vk, bool Extended);

    /// <summary>Virtual keys that must carry KEYEVENTF_EXTENDEDKEY when released, looked up by
    /// code alone (ReleaseAll only remembers the virtual key, not which browser key produced it).</summary>
    private static bool IsExtendedVk(ushort vk)
        => vk is 0x25 or 0x26 or 0x27 or 0x28      // arrows
            or 0x21 or 0x22 or 0x23 or 0x24        // page up/down, end, home
            or 0x2D or 0x2E                        // insert, delete
            or 0xA3 or 0xA5                        // right ctrl, right alt
            or 0x90 or 0x6F;                       // num lock, numpad divide

    /// <summary>Browser KeyboardEvent.code -> Windows virtual-key code. Built once; the ranges
    /// (letters, digits, function keys, numpad) are generated rather than typed out.</summary>
    private static readonly Dictionary<string, MappedKey> KeyMap = BuildKeyMap();

    private static Dictionary<string, MappedKey> BuildKeyMap()
    {
        var map = new Dictionary<string, MappedKey>(StringComparer.Ordinal);

        for (var c = 'A'; c <= 'Z'; c++) map[$"Key{c}"] = new MappedKey((ushort)c, false);          // VK_A..VK_Z == 'A'..'Z'
        for (var d = 0; d <= 9; d++) map[$"Digit{d}"] = new MappedKey((ushort)('0' + d), false);    // VK_0..VK_9 == '0'..'9'
        for (var f = 1; f <= 12; f++) map[$"F{f}"] = new MappedKey((ushort)(0x70 + f - 1), false);  // VK_F1 = 0x70
        for (var n = 0; n <= 9; n++) map[$"Numpad{n}"] = new MappedKey((ushort)(0x60 + n), false);  // VK_NUMPAD0 = 0x60

        // Editing and whitespace.
        map["Enter"] = new MappedKey(0x0D, false);
        map["Escape"] = new MappedKey(0x1B, false);
        map["Backspace"] = new MappedKey(0x08, false);
        map["Tab"] = new MappedKey(0x09, false);
        map["Space"] = new MappedKey(0x20, false);
        map["CapsLock"] = new MappedKey(0x14, false);

        // Modifiers. Left/right are distinct virtual keys; the right-hand ones are extended.
        map["ShiftLeft"] = new MappedKey(0xA0, false);
        map["ShiftRight"] = new MappedKey(0xA1, false);
        map["ControlLeft"] = new MappedKey(0xA2, false);
        map["ControlRight"] = new MappedKey(0xA3, true);
        map["AltLeft"] = new MappedKey(0xA4, false);
        map["AltRight"] = new MappedKey(0xA5, true);
        map["MetaLeft"] = new MappedKey(0x5B, false);
        map["MetaRight"] = new MappedKey(0x5C, false);
        map["ContextMenu"] = new MappedKey(0x5D, false);

        // Navigation — all extended, which is what distinguishes them from the numpad.
        map["ArrowLeft"] = new MappedKey(0x25, true);
        map["ArrowUp"] = new MappedKey(0x26, true);
        map["ArrowRight"] = new MappedKey(0x27, true);
        map["ArrowDown"] = new MappedKey(0x28, true);
        map["Home"] = new MappedKey(0x24, true);
        map["End"] = new MappedKey(0x23, true);
        map["PageUp"] = new MappedKey(0x21, true);
        map["PageDown"] = new MappedKey(0x22, true);
        map["Insert"] = new MappedKey(0x2D, true);
        map["Delete"] = new MappedKey(0x2E, true);

        // Punctuation (US layout positions; Windows translates by the active layout).
        map["Minus"] = new MappedKey(0xBD, false);
        map["Equal"] = new MappedKey(0xBB, false);
        map["BracketLeft"] = new MappedKey(0xDB, false);
        map["BracketRight"] = new MappedKey(0xDD, false);
        map["Backslash"] = new MappedKey(0xDC, false);
        map["Semicolon"] = new MappedKey(0xBA, false);
        map["Quote"] = new MappedKey(0xDE, false);
        map["Backquote"] = new MappedKey(0xC0, false);
        map["Comma"] = new MappedKey(0xBC, false);
        map["Period"] = new MappedKey(0xBE, false);
        map["Slash"] = new MappedKey(0xBF, false);

        // Numpad operators and the locks.
        map["NumpadMultiply"] = new MappedKey(0x6A, false);
        map["NumpadAdd"] = new MappedKey(0x6B, false);
        map["NumpadSubtract"] = new MappedKey(0x6D, false);
        map["NumpadDecimal"] = new MappedKey(0x6E, false);
        map["NumpadDivide"] = new MappedKey(0x6F, true);
        map["NumpadEnter"] = new MappedKey(0x0D, true);
        map["NumLock"] = new MappedKey(0x90, true);
        map["ScrollLock"] = new MappedKey(0x91, false);
        map["Pause"] = new MappedKey(0x13, false);
        map["PrintScreen"] = new MappedKey(0x2C, false);

        return map;
    }

    // ---- Win32 P/Invoke surface ------------------------------------------------------------

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // A union in C: all three overlap at the same offset. Sized by the largest member.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
