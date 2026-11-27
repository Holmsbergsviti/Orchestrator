// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the LAN relay's two wire messages — no sockets involved, just
//   the encode/decode round trip and, most importantly, that TryDecode on the payload
//   refuses anything whose path isn't exactly state/<thatMachineId>.json. That one check
//   is the entire safety story for a feature that otherwise trusts anything on the LAN
//   (see the file-level note in LanRelayProtocol.cs), so it gets the most scrutiny here.
// =====================================================================================

using System.Text;
using Orchestrator.Service.Services;
using Xunit;

namespace Orchestrator.Service.Tests;

public sealed class LanRelayProtocolTests
{
    // ---- LanRelayRequest ------------------------------------------------------------

    [Fact]
    public void Request_RoundTrips()
    {
        var original = new LanRelayRequest("m1", 54321);
        var decoded = LanRelayRequest.TryDecode(LanRelayRequest.Encode(original));
        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"t\":\"something-else\",\"machineId\":\"m1\",\"port\":1}")]
    [InlineData("{\"t\":\"relay-request\",\"machineId\":\"\",\"port\":1}")]
    [InlineData("{\"t\":\"relay-request\",\"machineId\":\"m1\",\"port\":0}")]
    [InlineData("{\"t\":\"relay-request\",\"machineId\":\"m1\",\"port\":70000}")]
    [InlineData("{\"t\":\"relay-request\",\"machineId\":\"m1\",\"port\":\"not-a-number\"}")]
    public void Request_RejectsMalformedOrOutOfRangeInput(string json)
        => Assert.Null(LanRelayRequest.TryDecode(json));

    // ---- LanRelayPayload -------------------------------------------------------------

    [Fact]
    public void Payload_RoundTrips()
    {
        var original = new LanRelayPayload("m1", "state/m1.json", "heartbeat: PC1 (m1) now", Encoding.UTF8.GetBytes("hello"));
        var decoded = LanRelayPayload.TryDecode(LanRelayPayload.Encode(original));
        Assert.NotNull(decoded);
        Assert.Equal(original.MachineId, decoded!.MachineId);
        Assert.Equal(original.Path, decoded.Path);
        Assert.Equal(original.CommitMessage, decoded.CommitMessage);
        Assert.Equal(original.Content, decoded.Content);
    }

    [Fact]
    public void Payload_RejectsAPathForADifferentMachineId()
    {
        // The core safety property: a payload can only ever target the state file for the
        // machine id it claims, never some other path — see the file-level note on why.
        var forged = LanRelayPayload.Encode(new LanRelayPayload("victim", "state/someone-else.json", "msg", new byte[] { 1 }));
        Assert.Null(LanRelayPayload.TryDecode(forged));
    }

    [Theory]
    [InlineData("state/../manifest.json")]
    [InlineData("manifest.json")]
    [InlineData("commands.json")]
    [InlineData("screenshots/m1/latest.jpg")]
    [InlineData("state/m1.json.sig")]
    public void Payload_RejectsAnyPathOutsideItsOwnStateFile(string path)
    {
        var json = $$"""{"machineId":"m1","path":"{{path}}","message":"m","content":"aGVsbG8="}""";
        Assert.Null(LanRelayPayload.TryDecode(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"machineId\":\"m1\",\"path\":\"state/m1.json\",\"message\":\"m\"}")]   // missing content
    [InlineData("{\"machineId\":\"m1\",\"path\":\"state/m1.json\",\"message\":\"m\",\"content\":\"not-base64!!\"}")]
    public void Payload_RejectsMalformedInput(string json)
        => Assert.Null(LanRelayPayload.TryDecode(json));
}
