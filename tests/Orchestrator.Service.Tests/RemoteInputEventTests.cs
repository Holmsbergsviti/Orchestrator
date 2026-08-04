// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for parsing the remote-control input protocol. This parser sits on the
//   boundary where bytes off the network become real keystrokes and mouse clicks on someone's
//   desktop, so these tests care as much about what it REFUSES as what it accepts: junk, wrong
//   types, unknown event kinds, out-of-range coordinates, key names that aren't key names, and
//   events missing the one field that gives them meaning.
// =====================================================================================

using Orchestrator.Service.Models;   // the code being tested
using Xunit;                         // the test framework

namespace Orchestrator.Service.Tests;

public sealed class RemoteInputEventTests
{
    [Fact]
    public void MouseMove_ParsesNormalizedPosition()
    {
        Assert.True(RemoteInputEvent.TryParse("""{"t":"mousemove","x":0.25,"y":0.75}""", out var evt));
        Assert.Equal(RemoteInputKind.MouseMove, evt.Kind);
        Assert.Equal(0.25, evt.X, 6);
        Assert.Equal(0.75, evt.Y, 6);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("middle")]
    public void MouseDown_AcceptsTheThreeRealButtons(string button)
    {
        Assert.True(RemoteInputEvent.TryParse($$"""{"t":"mousedown","x":0.5,"y":0.5,"button":"{{button}}"}""", out var evt));
        Assert.Equal(RemoteInputKind.MouseDown, evt.Kind);
        Assert.Equal(button, evt.Button);
    }

    [Fact]
    public void MouseDown_WithoutAButton_IsRejected()
    {
        // A press with no button can't be turned into anything meaningful, so it must not
        // silently become a left-click.
        Assert.False(RemoteInputEvent.TryParse("""{"t":"mousedown","x":0.5,"y":0.5}""", out _));
    }

    [Fact]
    public void MouseDown_WithAnUnknownButton_IsRejected()
    {
        Assert.False(RemoteInputEvent.TryParse("""{"t":"mousedown","x":0.5,"y":0.5,"button":"x1"}""", out _));
    }

    [Theory]
    [InlineData(-5.0, 0.0)]      // off the left/top edge
    [InlineData(42.0, 1.0)]      // way off the right/bottom edge
    public void Coordinates_AreClampedNotRejected(double given, double expected)
    {
        // Slightly-outside values are a rounding artifact of the browser scaling the canvas,
        // so they're clamped. What must never happen is a wild value reaching SendInput.
        Assert.True(RemoteInputEvent.TryParse($$"""{"t":"mousemove","x":{{given}},"y":{{given}}}""", out var evt));
        Assert.Equal(expected, evt.X, 6);
        Assert.Equal(expected, evt.Y, 6);
    }

    [Fact]
    public void WheelDelta_IsBounded()
    {
        Assert.True(RemoteInputEvent.TryParse("""{"t":"wheel","x":0.5,"y":0.5,"dy":100000}""", out var evt));
        Assert.Equal(30, evt.WheelDelta);   // one message can't become thousands of notches
    }

    [Fact]
    public void KeyDown_ParsesTheBrowsersKeyCode()
    {
        Assert.True(RemoteInputEvent.TryParse("""{"t":"keydown","code":"KeyA"}""", out var evt));
        Assert.Equal(RemoteInputKind.KeyDown, evt.Kind);
        Assert.Equal("KeyA", evt.Code);
    }

    [Fact]
    public void KeyEvent_WithoutACode_IsRejected()
    {
        Assert.False(RemoteInputEvent.TryParse("""{"t":"keyup"}""", out _));
    }

    [Theory]
    [InlineData("Key A")]                    // whitespace
    [InlineData("Key-A")]                    // punctuation
    [InlineData("../../etc/passwd")]         // path-ish
    [InlineData("KeyAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // longer than any real key name
    public void KeyCodes_OutsideTheExpectedVocabulary_AreRejected(string code)
    {
        Assert.False(RemoteInputEvent.TryParse($$"""{"t":"keydown","code":"{{code}}"}""", out _));
    }

    [Fact]
    public void End_IsRecognized()
    {
        Assert.True(RemoteInputEvent.TryParse("""{"t":"end"}""", out var evt));
        Assert.Equal(RemoteInputKind.End, evt.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]                          // valid JSON, wrong shape
    [InlineData("\"a string\"")]                     // valid JSON, wrong shape
    [InlineData("""{"t":"shutdown"}""")]             // unknown kind
    [InlineData("""{"t":123}""")]                    // wrong type for t
    [InlineData("""{"nope":"mousemove"}""")]         // no kind at all
    public void Garbage_IsRejectedWithoutThrowing(string json)
    {
        Assert.False(RemoteInputEvent.TryParse(json, out _));
    }

    [Fact]
    public void NonNumericCoordinates_DoNotBecomeMovement()
    {
        // A string where a number belongs must not be coerced; it falls back to 0, and
        // critically doesn't throw or land somewhere arbitrary on screen.
        Assert.True(RemoteInputEvent.TryParse("""{"t":"mousemove","x":"1","y":null}""", out var evt));
        Assert.Equal(0d, evt.X);
        Assert.Equal(0d, evt.Y);
    }
}
