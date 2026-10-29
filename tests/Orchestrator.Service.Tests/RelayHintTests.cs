// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks the "where to dial for a live screen session" hint end to end. On the agent side:
//   which values are accepted (they end up on a command line, so anything odd is dropped) and
//   that a hint is only used for the exact session it names. On the console side, against REAL
//   git (a bare "GitHub" plus the operator's clone): pressing Remote publishes a signed hint on
//   a throwaway one-commit branch — never on main — a still-pending session keeps it, and once
//   the session is over it is deleted from GitHub and left in no history anywhere.
// =====================================================================================

using System.Diagnostics;                          // running git
using System.Text;                                 // Encoding.UTF8
using System.Text.Json;                            // reading the hint back
using Microsoft.Extensions.Logging.Abstractions;   // NullLogger
using Orchestrator.Console;                        // ControlRepo / RelayAdvert / ControlSigning
using Orchestrator.Service.Models;                 // RelayHint
using Orchestrator.Service.Services;               // ControlFileVerifier
using Xunit;                                       // the test framework

namespace Orchestrator.Service.Tests;

public sealed class RelayHintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-hint-" + Guid.NewGuid().ToString("N"));
    private string Remote => Path.Combine(_root, "github.git");
    private string Mine => Path.Combine(_root, "mine");
    private readonly string _keyPath;
    private readonly RelayHub _hub = new(NullLogger<RelayHub>.Instance);
    private const string Thumb = "99C856C53544222A3D82A4E56A6B903D6F4C8C0E";

    public RelayHintTests()
    {
        Directory.CreateDirectory(_root);
        _keyPath = Path.Combine(_root, "key", "signing-key.pem");
        new ControlSigning(_keyPath).LoadOrCreate();
        Git(_root, "init", "-q", "--bare", "-b", "main", Remote);
        Git(_root, "clone", "-q", Remote, Mine);
        Configure(Mine);
        File.WriteAllText(Path.Combine(Mine, "commands.json"), "{}");
        File.WriteAllText(Path.Combine(Mine, "manifest.json"), "{\"programs\":[]}");
        Git(Mine, "add", "-A"); Git(Mine, "commit", "-qm", "init"); Git(Mine, "push", "-q", "origin", "HEAD:main");

        // One machine has reported a heartbeat (with a remote-control key) on the fleet-state branch.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "-q", Remote, other);
        Configure(other);
        Git(other, "checkout", "-q", "--orphan", "fleet-state");
        Git(other, "rm", "-rfq", ".");
        Directory.CreateDirectory(Path.Combine(other, "state"));
        File.WriteAllText(Path.Combine(other, "state", "m1.json"),
            "{\"machineId\":\"m1\",\"hostname\":\"PC-ONE\",\"relayPublicKey\":\"key-of-m1\",\"lastSeenUtc\":\"2026-10-29T20:00:00Z\"}");
        Git(other, "add", "-A"); Git(other, "commit", "-qm", "heartbeat"); Git(other, "push", "-q", "origin", "fleet-state");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private static void Configure(string repo)
    {
        Git(repo, "config", "user.email", "t@example.com");
        Git(repo, "config", "user.name", "tester");
        Git(repo, "config", "commit.gpgsign", "false");
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)}: {e}");
        return o.Trim();
    }

    private string OnGitHub(params string[] args) => Git(_root, new[] { "--git-dir=" + Remote }.Concat(args).ToArray());
    private bool HintBranchExists() => OnGitHub("branch", "--list", "relay-hint").Length > 0;

    private ControlRepo Console(RelayAdvert? advert, RelayHub? hub = null)
        => new(new ConsoleOptions { ControlRepoPath = Mine, Remote = "origin", MainBranch = "main", SigningKeyPath = _keyPath },
               hub ?? _hub,
               new RelayKeyStore(Path.Combine(_root, "pins.json"), NullLogger<RelayKeyStore>.Instance),
               new ControlSigning(_keyPath),
               NullLogger<ControlRepo>.Instance,
               advert);

    private static readonly RelayAdvert Advert = new("wss://operator-mac.local:5080", Thumb);

    private async Task<ControlRepo> ConsoleWithSignedMain(RelayAdvert? advert)
    {
        var console = Console(advert);
        Assert.True((await console.SignNowAsync(default)).Ok);   // the console won't build on unsigned control files
        return console;
    }

    // ---- agent side: what is accepted -------------------------------------------------

    private static RelayHintFile Hint(string machine, string id, string url, string? thumb = Thumb)
        => new() { Sessions = { [machine] = new RelayHintEntry { Id = id, RelayUrl = url, RelayCertThumbprint = thumb } } };

    [Fact]
    public void Pick_GivesTheAddressForTheExactSession()
    {
        var hint = Hint("m1", "abc", "wss://operator-mac.local:5080");
        Assert.Equal(("wss://operator-mac.local:5080", Thumb), RelayHint.Pick(hint, "m1", "abc"));
        Assert.Equal(("wss://operator-mac.local:5080", Thumb), RelayHint.Pick(hint, "M1", "abc"));   // machine ids compare case-insensitively
        Assert.Null(RelayHint.Pick(hint, "m1", "another-session"));   // a hint for some other session is not ours
        Assert.Null(RelayHint.Pick(hint, "m2", "abc"));
        Assert.Null(RelayHint.Pick(null, "m1", "abc"));
    }

    [Theory]
    [InlineData("wss://host.local:5080")]
    [InlineData("ws://192.168.1.20:5080")]
    [InlineData("wss://[fe80::1]:5080")]
    [InlineData("wss://hostonly")]
    public void WellFormedAddresses_AreAccepted(string url) => Assert.True(RelayHint.IsWellFormedUrl(url));

    [Theory]
    [InlineData("")]
    [InlineData("https://host.local:5080")]
    [InlineData("wss://host.local:5080/relay")]          // a path isn't part of the address
    [InlineData("wss://user:pw@host.local:5080")]        // credentials neither
    [InlineData("wss://host.local:5080 --evil")]         // it lands on a command line
    [InlineData("wss://host.local:5080\" /x")]
    [InlineData("wss://host.local;calc")]
    public void OddAddresses_AreDropped(string url)
    {
        Assert.False(RelayHint.IsWellFormedUrl(url));
        Assert.Null(RelayHint.Pick(Hint("m1", "abc", url), "m1", "abc"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("99C856C53544222A3D82A4E56A6B903D6F4C8C0E", true)]
    [InlineData("99:C8:56:C5", false)]
    [InlineData("99C856C53544222A3D82A4E56A6B903D6F4C8C0", false)]    // 39 digits
    [InlineData("ZZC856C53544222A3D82A4E56A6B903D6F4C8C0E", false)]
    public void Thumbprints_AreExactlyFortyHexDigitsOrEmpty(string? thumb, bool ok)
        => Assert.Equal(ok, RelayHint.IsWellFormedThumbprint(thumb));

    // ---- console side, against real git -------------------------------------------------

    [Fact]
    public async Task PressingRemote_PublishesASignedHintOnAOneCommitBranch_NeverOnMain()
    {
        var console = await ConsoleWithSignedMain(Advert);
        var mainBefore = OnGitHub("rev-parse", "main");

        var result = await console.StartRemoteSessionAsync("m1", default);

        Assert.True(result.Ok, result.Message);
        Assert.True(HintBranchExists());
        Assert.Equal("1", OnGitHub("rev-list", "--count", "relay-hint"));                 // one commit...
        Assert.Equal("", OnGitHub("log", "-1", "--format=%P", "relay-hint"));             // ...with no parent
        // The address is nowhere in main's history, only on the throwaway branch.
        Assert.DoesNotContain("operator-mac", OnGitHub("log", "-p", "main"));
        Assert.NotEqual(mainBefore, OnGitHub("rev-parse", "main"));                       // (main moved: the session request itself)
        Assert.True(new GitRepo(Mine).IsClean());

        // What an agent would download: the entry for THIS session, signed by the operator's key.
        // (`git show` trims the trailing newline the console wrote, so put it back before checking.)
        var content = Encoding.UTF8.GetBytes(OnGitHub("show", "relay-hint:relay-hint.json") + "\n");
        var sig = Encoding.UTF8.GetBytes(OnGitHub("show", "relay-hint:relay-hint.json.sig") + "\n");
        var agent = new ControlFileVerifier(new ControlSigning(_keyPath).PublicKeyBase64,
            Path.Combine(_root, "agent-state.json"), NullLogger<ControlFileVerifier>.Instance);
        Assert.Null(agent.Check("relay-hint.json", content, sig));
        var file = JsonSerializer.Deserialize<RelayHintFile>(content)!;
        Assert.Equal(("wss://operator-mac.local:5080", Thumb), RelayHint.Pick(file, "m1", result.SessionId!));
    }

    [Fact]
    public async Task APendingSession_KeepsItsHint_AndAFinishedOneTakesTheBranchWithIt()
    {
        var console = await ConsoleWithSignedMain(Advert);
        var started = await console.StartRemoteSessionAsync("m1", default);
        Assert.True(started.Ok, started.Message);

        await console.SweepRelayHintAsync(default);
        Assert.True(HintBranchExists());   // the agent hasn't had its chance yet

        // The session ends (or was never picked up): the relay forgets it.
        _hub.RegisterPending(started.SessionId!, "m1", "key-of-m1", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        await console.SweepRelayHintAsync(default);

        Assert.False(HintBranchExists());                       // nothing left on GitHub
        Assert.DoesNotContain("relay-hint", OnGitHub("for-each-ref", "refs/heads"));
        Assert.True(new GitRepo(Mine).IsClean());
    }

    [Fact]
    public async Task ARestartedConsole_ClearsAHintItsPredecessorLeftBehind()
    {
        var first = await ConsoleWithSignedMain(Advert);
        Assert.True((await first.StartRemoteSessionAsync("m1", default)).Ok);
        Assert.True(HintBranchExists());

        // The console died with the session open; the new process has an empty relay and no memory of it.
        var second = Console(Advert, hub: new RelayHub(NullLogger<RelayHub>.Instance));
        await second.NoteStartupAsync(default);
        await second.SweepRelayHintAsync(default);

        Assert.False(HintBranchExists());
    }

    [Fact]
    public async Task WithoutAnAddressToAdvertise_NothingIsPublished_AndTheSessionStillStarts()
    {
        var console = await ConsoleWithSignedMain(advert: null);   // e.g. a console bound to localhost
        var result = await console.StartRemoteSessionAsync("m1", default);

        Assert.True(result.Ok, result.Message);
        Assert.False(HintBranchExists());   // PCs use the Orchestrator:RelayUrl they were installed with
    }

    [Fact]
    public async Task ASessionThatCantBeRequested_LeavesNoHintBehind()
    {
        var console = await ConsoleWithSignedMain(Advert);
        var result = await console.StartRemoteSessionAsync("no-such-machine", default);

        Assert.False(result.Ok);
        Assert.False(HintBranchExists());
    }

    // ---- what the console advertises -----------------------------------------------------

    [Theory]
    [InlineData("https://0.0.0.0:5080", "Vlads-MacBook-Pro", true, "wss://Vlads-MacBook-Pro.local:5080")]     // macOS: the name Windows can find
    [InlineData("https://0.0.0.0:5080", "DESKTOP-OPS", false, "wss://DESKTOP-OPS:5080")]                      // elsewhere the name as is
    [InlineData("https://*:5443", "box.example.net", true, "wss://box.example.net:5443")]                      // already dotted: left alone
    [InlineData("http://0.0.0.0:5080", "pc", false, "ws://pc:5080")]
    [InlineData("https://ops.example.net:5080", "ignored", true, "wss://ops.example.net:5080")]                // a specific host is used as written
    public void TheConsoleAdvertisesItsNameNotAnAddress(string urls, string host, bool mac, string expected)
        => Assert.Equal(expected, RelayAdvert.From(new ConsoleOptions(), urls, Thumb, host, mac)!.Url);

    [Fact]
    public void ALocalhostOnlyConsoleAdvertisesNothing()
    {
        Assert.Null(RelayAdvert.From(new ConsoleOptions(), "http://localhost:5080", null, "pc", false));
        Assert.Null(RelayAdvert.From(new ConsoleOptions(), "https://127.0.0.1:5080", null, "pc", false));
    }

    [Fact]
    public void AnExplicitAddressWinsIfItIsUsable()
    {
        var opt = new ConsoleOptions { RelayAdvertiseUrl = "wss://ops.tailnet.example:5080/" };
        Assert.Equal(("wss://ops.tailnet.example:5080", Thumb), (RelayAdvert.From(opt, "https://0.0.0.0:5080", Thumb, "pc", false)!.Url, Thumb));
        opt.RelayAdvertiseUrl = "https://not-a-websocket";
        Assert.Null(RelayAdvert.From(opt, "https://0.0.0.0:5080", Thumb, "pc", false));
    }

    [Fact]
    public void TheThumbprintTravelsWithTheAddress()
        => Assert.Equal(Thumb, RelayAdvert.From(new ConsoleOptions(), "https://0.0.0.0:5080", Thumb, "pc", false)!.Thumbprint);
}
