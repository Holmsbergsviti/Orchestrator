// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks signed control files end to end: the console signs (ControlSigning), the agent
//   verifies (ControlFileVerifier), and a signature made the way publish-agent.yml makes it
//   (openssl, recorded below as a fixture) verifies too. Also covers what must be refused:
//   no signature, edited content, another key, a signature moved to another file, and an
//   older genuine file replayed after a newer one.
// =====================================================================================

using System.Text;                                 // Encoding.UTF8
using Microsoft.Extensions.Logging.Abstractions;   // NullLogger
using Orchestrator.Console;                        // ControlSigning
using Orchestrator.Service.Services;               // ControlFileVerifier
using Xunit;                                       // the test framework

namespace Orchestrator.Service.Tests;

public sealed class SignedControlFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "signing-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private ControlSigning Console(string name = "op")
    {
        var s = new ControlSigning(Path.Combine(_dir, name, "signing-key.pem"));
        s.LoadOrCreate();
        return s;
    }

    private ControlFileVerifier Agent(string? publicKey, string state = "state.json")
        => new(publicKey, Path.Combine(_dir, state), NullLogger<ControlFileVerifier>.Instance);

    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("{\"programs\":[]}");

    [Fact]
    public void ConsoleSignature_IsAcceptedByTheAgent()
    {
        var op = Console();
        var sig = op.Sign("manifest.json", Manifest, null);
        Assert.Null(Agent(op.PublicKeyBase64).Check("manifest.json", Manifest, sig));
        Assert.Equal(SignatureState.Valid, op.Check("manifest.json", Manifest, sig));
    }

    [Fact]
    public void OpensslSignature_AsPublishAgentYmlMakesIt_IsAccepted()
    {
        // Made with the exact openssl commands in publish-agent.yml's "Record the release" step.
        const string pub = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE05SLcAQz2/o3Y7D8Yx6b3K3oN8Cvbv+JdAG6awd3up0Qk2LzpezNJ04XY2zDRkf6tpZyvAdEXu54s76uwytqWQ==";
        var content = Convert.FromBase64String("ewogICJzaGEyNTYiOiAiYWJjIiwKICAiZW5hYmxlZCI6IHRydWUKfQo=");
        var sig = Convert.FromBase64String("ewogICJzaWduZWRVdGMiOiAiMjAyNi0xMC0yOVQyMDoxNTowMFoiLAogICJzaWduYXR1cmUiOiAiTUVVQ0lRQ1RHN3FLWGN5aTVDeStIT3FoWHVRR0t1eGpoRFhiL1B3a3EwWnQwSGlwdmdJZ2R4enJ0VHpWR0ZOaFdiK3ZMajkvSERObHBsREFOQVM4WUphU0tGTU9iYjQ9Igp9Cg==");
        Assert.Null(Agent(pub).Check("agent.json", content, sig));
    }

    [Fact]
    public void Unsigned_Edited_OtherKey_OrMovedSignature_AreRefused()
    {
        var op = Console();
        var agent = Agent(op.PublicKeyBase64);
        var sig = op.Sign("manifest.json", Manifest, null);

        Assert.NotNull(agent.Check("manifest.json", Manifest, null));                                    // no .sig at all
        Assert.NotNull(agent.Check("manifest.json", Encoding.UTF8.GetBytes("{\"programs\":[1]}"), sig));  // edited after signing
        Assert.NotNull(agent.Check("commands.json", Manifest, sig));                                      // signature moved to another file
        var attacker = Console("attacker");
        Assert.NotNull(agent.Check("manifest.json", Manifest, attacker.Sign("manifest.json", Manifest, null)));   // someone else's key
        Assert.NotNull(agent.Check("manifest.json", Manifest, Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void OlderGenuineFile_ReplayedAfterANewerOne_IsRefused()
    {
        var op = Console();
        var agent = Agent(op.PublicKeyBase64);
        var oldSig = op.Sign("manifest.json", Manifest, null);
        var newer = Encoding.UTF8.GetBytes("{\"programs\":[\"new\"]}");
        var newSig = op.Sign("manifest.json", newer, oldSig);   // always later than the one before

        Assert.Null(agent.Check("manifest.json", newer, newSig));
        Assert.NotNull(agent.Check("manifest.json", Manifest, oldSig));   // the replay
        Assert.Null(agent.Check("manifest.json", newer, newSig));         // the current one still fine
        Assert.NotNull(Agent(op.PublicKeyBase64).Check("manifest.json", Manifest, oldSig));   // remembered across restarts
    }

    [Fact]
    public void SigningTime_NeverGoesBackwards()
    {
        // A previous signature "from the future" (a console clock that was ahead) must not make
        // the next signature look older — agents would refuse it as a replay.
        var op = Console();
        var future = Encoding.UTF8.GetBytes(
            "{\"signedUtc\":\"2099-01-01T00:00:00Z\",\"signature\":\"x\"}");
        var sig = op.Sign("manifest.json", Manifest, future);
        Assert.Contains("2099-01-01T00:00:01Z", Encoding.UTF8.GetString(sig));
    }

    [Fact]
    public void BuildWithoutAKey_EnforcesNothing()
    {
        var agent = Agent(publicKey: null);
        Assert.False(agent.Enforced);
        Assert.Null(agent.Check("manifest.json", Manifest, null));   // exactly today's behaviour
    }

    [Fact]
    public void WireFormats_Match()
        => Assert.Equal(ControlSignature.Message("agent.json", "2026-10-29T20:15:00Z", Manifest),
                        ControlSigning.Message("agent.json", "2026-10-29T20:15:00Z", Manifest));
}
