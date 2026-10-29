// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks that one fleet machine can't answer for another machine's remote session.
//   The agent signs with RelayIdentity (service) and the console verifies with
//   RelaySignature (console) — two copies of the same wire format, tested against each
//   other so neither can drift. Also covers the console's key pinning: first key pinned,
//   a different key refused until trusted, an unreadable pin file failing closed.
// =====================================================================================

using Microsoft.Extensions.Logging.Abstractions;   // NullLogger
using Orchestrator.Console;                        // RelaySignature / RelayKeyStore
using Orchestrator.Service.Models;                 // Heartbeat
using Orchestrator.Service.Services;               // RelayIdentity / RelayProof
using Xunit;                                       // the test framework

namespace Orchestrator.Service.Tests;

public sealed class RelayIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private RelayIdentity Identity(string name = "a")
        => new(Path.Combine(_dir, name, "relay-key.pem"), NullLogger<RelayIdentity>.Instance);

    private RelayKeyStore Store() => new(Path.Combine(_dir, "pins.json"), NullLogger<RelayKeyStore>.Instance);

    [Fact]
    public void AgentSignature_VerifiesOnTheConsole()
    {
        var id = Identity();
        var proof = id.Sign("Machine-A", "0123abcd");
        Assert.True(RelaySignature.Verify(id.PublicKey, "machine-a", "0123abcd", proof));   // machine ids compare case-insensitively
    }

    [Fact]
    public void Signature_IsBoundToTheMachineAndTheSession()
    {
        var id = Identity();
        var proof = id.Sign("machine-a", "s1");
        Assert.False(RelaySignature.Verify(id.PublicKey, "machine-b", "s1", proof));   // replayed for another machine
        Assert.False(RelaySignature.Verify(id.PublicKey, "machine-a", "s2", proof));   // replayed for another session
    }

    [Fact]
    public void AnotherMachinesKey_IsRejected()
    {
        // The attack this exists for: machine B read A's session id from commands.json and
        // signs it with its own key. The console checks against A's pinned key.
        var a = Identity("a");
        var b = Identity("b");
        var forged = b.Sign("machine-a", "s1");
        Assert.False(RelaySignature.Verify(a.PublicKey, "machine-a", "s1", forged));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("abcd")]
    public void MalformedProof_IsRejectedNotThrown(string? proof)
        => Assert.False(RelaySignature.Verify(Identity().PublicKey, "m", "s", proof));

    [Fact]
    public void Key_PersistsAcrossRestarts()
    {
        var first = Identity().PublicKey;
        var second = Identity().PublicKey;   // a fresh instance reads the same file
        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void WireFormats_Match()
        => Assert.Equal(RelayProof.Message("Machine-A ", "s1"), RelaySignature.Message("machine-a", "s1"));

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", true)]
    [InlineData("abc def", false)]          // a space would split the task's command line
    [InlineData("abc\" & calc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SessionIds_MustLookLikeWhatTheConsoleGenerates(string? id, bool ok)
        => Assert.Equal(ok, RelayProof.IsWellFormedSessionId(id));

    [Fact]
    public void Pinning_FirstKeyTrusted_ChangedKeyRefusedUntilTrusted()
    {
        var store = Store();
        Assert.Equal(RelayKeyStatus.Trusted, store.Check("m1", "key-1"));   // first sight pins it
        Assert.Equal(RelayKeyStatus.Trusted, store.Check("M1", "key-1"));
        Assert.Equal(RelayKeyStatus.Changed, store.Check("m1", "key-2"));   // impersonator or reinstall
        Assert.Equal("key-1", store.Pinned("m1"));                          // the pin is NOT replaced by checking

        store.Trust("m1", "key-2");
        Assert.Equal(RelayKeyStatus.Trusted, store.Check("m1", "key-2"));
        Assert.Equal(RelayKeyStatus.Changed, Store().Check("m1", "key-1"));  // survives a console restart
    }

    [Fact]
    public void Pinning_MissingKey_IsNotPinned()
    {
        var store = Store();
        Assert.Equal(RelayKeyStatus.Missing, store.Check("m1", null));
        Assert.Equal(RelayKeyStatus.Trusted, store.Check("m1", "key-1"));   // the first REAL key still pins
    }

    [Fact]
    public void CorruptPinFile_FailsClosed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "pins.json"), "{ not json");
        // Starting over would silently re-pin whatever each machine reports right now.
        Assert.Throws<InvalidOperationException>(() => Store().Check("m1", "key-1"));
    }

    [Fact]
    public void NewKey_ChangesTheHeartbeatSignature()
    {
        // Otherwise a freshly created key would sit unpublished until something else changed.
        var hb = new Heartbeat { MachineId = "m1" };
        var before = hb.Signature;
        hb.RelayPublicKey = "key";
        Assert.NotEqual(before, hb.Signature);
    }

    [Fact]
    public void SwitchingToken_ChangesTheHeartbeatSignature()
    {
        // Moving a machine to a read-only token must clear the console's warning promptly.
        var hb = new Heartbeat { MachineId = "m1", ControlTokenCanWrite = true };
        var before = hb.Signature;
        hb.ControlTokenCanWrite = false;
        Assert.NotEqual(before, hb.Signature);
    }

    [Fact]
    public void StateRepo_DefaultsToTheControlRepo()
    {
        var c = new OrchestratorConfig { RepoOwner = "me", RepoName = "control", GitHubToken = "read" };
        Assert.False(c.UsesSeparateStateRepo);
        Assert.Equal("me", c.EffectiveStateOwner);
        Assert.Equal("control", c.EffectiveStateRepo);
        Assert.Equal("read", c.EffectiveStateToken);   // older installs keep working unchanged
    }

    [Fact]
    public void StateRepo_WhenSet_GetsItsOwnRepoAndToken()
    {
        var c = new OrchestratorConfig
        {
            RepoOwner = "me", RepoName = "control", GitHubToken = "read",
            StateRepoOwner = "me", StateRepoName = "control-state", StateToken = "write"
        };
        Assert.True(c.UsesSeparateStateRepo);
        Assert.Equal("control-state", c.EffectiveStateRepo);
        Assert.Equal("write", c.EffectiveStateToken);
    }
}
