// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks the "this click came from the operator" stamp. The on-PC banner ends the session when
//   clicked, but must ignore a click the operator sent remotely (it is invisible to them and sits
//   where a window's close button is), so it recognises our own injected input by this stamp.
// =====================================================================================

using Orchestrator.Service.Services;   // RemoteInputInjector
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;

public class InjectedInputMarkerTests
{
    [Fact]
    public void OurStamp_IsRecognised()
        => Assert.True(RemoteInputInjector.IsInjected(RemoteInputInjector.InjectedMarker));

    [Theory]
    [InlineData(0)]                  // a real mouse click carries no extra info
    [InlineData(0xFF515700)]         // what touch and pen input carry
    [InlineData(-1)]
    public void OtherInput_IsNotTreatedAsInjected(long extra)
        => Assert.False(RemoteInputInjector.IsInjected(extra));
}
