// =====================================================================================
// FILE PURPOSE (in plain terms):
//   One mouse or keyboard action sent from the operator's browser to this machine during a
//   live remote-control session, plus the parser that turns the wire format into it. Frames
//   travel the other way as binary WebSocket messages; input events come back as small JSON
//   TEXT messages, which is what keeps the two directions impossible to confuse.
//
//   Mouse positions are NORMALIZED (0..1 across the whole virtual desktop) rather than pixels.
//   The browser is looking at a downscaled JPEG whose size it doesn't control and which can
//   change mid-session, and the desktop may span several monitors with a negative origin —
//   fractions survive all of that, and Windows' own absolute-positioning API happens to want
//   the same thing.
//
//   Parsing is deliberately paranoid and lives here, apart from the injector: this is data
//   arriving from the network that turns directly into synthetic keystrokes on someone's
//   desktop, so anything malformed, out of range, or unrecognized is dropped rather than
//   guessed at. Being a pure class also means it can be unit-tested off Windows.
// =====================================================================================

using System.Text.Json;   // JsonDocument for tolerant parsing

namespace Orchestrator.Service.Models;

/// <summary>What the operator did. Anything we don't recognize parses as Unknown and is ignored.</summary>
public enum RemoteInputKind
{
    Unknown = 0,
    MouseMove,
    MouseDown,
    MouseUp,
    Wheel,
    KeyDown,
    KeyUp,
    End,      // the operator asked to end the session
    Renew     // the operator asked for another grant of session time
}

public sealed class RemoteInputEvent
{
    public RemoteInputKind Kind { get; init; }

    /// <summary>Horizontal position as a fraction (0..1) of the virtual desktop's width.</summary>
    public double X { get; init; }
    /// <summary>Vertical position as a fraction (0..1) of the virtual desktop's height.</summary>
    public double Y { get; init; }

    /// <summary>"left", "right" or "middle" for button events; empty otherwise.</summary>
    public string Button { get; init; } = "";

    /// <summary>Wheel movement in notches; positive scrolls up (away from the user).</summary>
    public int WheelDelta { get; init; }

    /// <summary>The browser's KeyboardEvent.code, e.g. "KeyA", "Enter", "ShiftLeft".</summary>
    public string Code { get; init; } = "";

    /// <summary>Parse one wire message. Returns false for anything malformed or unrecognized —
    /// callers drop it silently rather than acting on a half-understood event.</summary>
    public static bool TryParse(string json, out RemoteInputEvent evt)
    {
        evt = new RemoteInputEvent();
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("t", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                return false;

            var kind = typeProp.GetString() switch
            {
                "mousemove" => RemoteInputKind.MouseMove,
                "mousedown" => RemoteInputKind.MouseDown,
                "mouseup" => RemoteInputKind.MouseUp,
                "wheel" => RemoteInputKind.Wheel,
                "keydown" => RemoteInputKind.KeyDown,
                "keyup" => RemoteInputKind.KeyUp,
                "end" => RemoteInputKind.End,
                "renew" => RemoteInputKind.Renew,
                _ => RemoteInputKind.Unknown
            };
            if (kind == RemoteInputKind.Unknown) return false;

            evt = new RemoteInputEvent
            {
                Kind = kind,
                // Clamped, not rejected: a click a fraction of a pixel outside the canvas is a
                // rounding artifact of the browser's scaling, not an attack or a bug worth losing.
                X = Clamp01(ReadDouble(doc.RootElement, "x")),
                Y = Clamp01(ReadDouble(doc.RootElement, "y")),
                Button = ReadButton(doc.RootElement),
                // Bounded so one absurd message can't turn into thousands of scroll notches.
                WheelDelta = Math.Clamp((int)ReadDouble(doc.RootElement, "dy"), -30, 30),
                Code = ReadCode(doc.RootElement)
            };

            // A button event without a button, or a key event without a key, is meaningless.
            if (kind is RemoteInputKind.MouseDown or RemoteInputKind.MouseUp && evt.Button.Length == 0) return false;
            if (kind is RemoteInputKind.KeyDown or RemoteInputKind.KeyUp && evt.Code.Length == 0) return false;
            return true;
        }
        catch (JsonException)
        {
            return false;   // not JSON at all
        }
    }

    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0d, 1d) : 0d;

    private static double ReadDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : 0d;

    private static string ReadButton(JsonElement root)
    {
        if (!root.TryGetProperty("button", out var p) || p.ValueKind != JsonValueKind.String) return "";
        return p.GetString() switch
        {
            "left" => "left",
            "right" => "right",
            "middle" => "middle",
            _ => ""    // anything else is not a button we inject
        };
    }

    private static string ReadCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var p) || p.ValueKind != JsonValueKind.String) return "";
        var code = p.GetString() ?? "";
        // Key names are a short, fixed vocabulary. Cap the length and allow only letters and
        // digits so nothing strange reaches the lookup table, however it got onto the wire.
        if (code.Length is 0 or > 32) return "";
        foreach (var c in code)
            if (!char.IsAsciiLetterOrDigit(c)) return "";
        return code;
    }
}
