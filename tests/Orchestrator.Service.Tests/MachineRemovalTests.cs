// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks removing a machine from the console, against REAL git (a bare "GitHub" with a
//   fleet-state branch full of heartbeats): "only remove from the list" deletes just that
//   machine's heartbeat and screenshots and leaves everyone else's alone; ids that aren't plain
//   machine ids are refused before anything is touched; and the "uninstall" command is written
//   into the signed commands.json with a short expiry, so a machine reinstalled later can't find
//   an old uninstall waiting for it.
// =====================================================================================

using System.Diagnostics;                          // running git
using System.Text.Json.Nodes;                      // reading commands.json back
using Microsoft.Extensions.Logging.Abstractions;   // NullLogger
using Orchestrator.Console;                        // ControlRepo
using Orchestrator.Service.Models;                 // OneShotExpiry
using Xunit;                                       // the test framework

namespace Orchestrator.Service.Tests;

public sealed class MachineRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "machine-removal-" + Guid.NewGuid().ToString("N"));
    private string Remote => Path.Combine(_root, "github.git");
    private string Mine => Path.Combine(_root, "mine");
    private readonly string _keyPath;
    private readonly RelayKeyStore _keys;

    public MachineRemovalTests()
    {
        Directory.CreateDirectory(_root);
        _keyPath = Path.Combine(_root, "key", "signing-key.pem");
        new ControlSigning(_keyPath).LoadOrCreate();
        _keys = new RelayKeyStore(Path.Combine(_root, "pins.json"), NullLogger<RelayKeyStore>.Instance);
        Git(_root, "init", "-q", "--bare", "-b", "main", Remote);
        Git(_root, "clone", "-q", Remote, Mine);
        Configure(Mine);
        File.WriteAllText(Path.Combine(Mine, "commands.json"), "{}");
        File.WriteAllText(Path.Combine(Mine, "manifest.json"), "{\"programs\":[]}");
        Git(Mine, "add", "-A"); Git(Mine, "commit", "-qm", "init"); Git(Mine, "push", "-q", "origin", "HEAD:main");

        // The fleet-state branch, written the way agents write it: a heartbeat per machine, plus a screenshot.
        var agents = Path.Combine(_root, "agents");
        Git(_root, "clone", "-q", Remote, agents);
        Configure(agents);
        Git(agents, "checkout", "-q", "--orphan", "fleet-state");
        Git(agents, "rm", "-rfq", ".");
        Directory.CreateDirectory(Path.Combine(agents, "state"));
        Directory.CreateDirectory(Path.Combine(agents, "screenshots", "old-vm"));
        foreach (var id in new[] { "old-vm", "keeper" })
            File.WriteAllText(Path.Combine(agents, "state", id + ".json"),
                "{\"machineId\":\"" + id + "\",\"hostname\":\"" + id.ToUpperInvariant() + "\",\"relayPublicKey\":\"key-of-" + id + "\"}");
        File.WriteAllText(Path.Combine(agents, "screenshots", "old-vm", "latest.jpg"), "jpeg");
        Git(agents, "add", "-A"); Git(agents, "commit", "-qm", "heartbeats"); Git(agents, "push", "-q", "origin", "fleet-state");
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

    private ControlRepo Console()
        => new(new ConsoleOptions { ControlRepoPath = Mine, Remote = "origin", MainBranch = "main", SigningKeyPath = _keyPath },
               new RelayHub(NullLogger<RelayHub>.Instance), _keys, new ControlSigning(_keyPath), NullLogger<ControlRepo>.Instance);

    [Fact]
    public async Task Forget_RemovesThatMachinesHeartbeatAndScreenshots_AndNobodyElses()
    {
        _keys.Check("old-vm", "key-of-old-vm");   // as the console would have pinned it on first sight
        var result = await Console().ForgetMachineAsync("old-vm", default);

        Assert.True(result.Ok, result.Message);
        var files = OnGitHub("ls-tree", "-r", "--name-only", "fleet-state");
        Assert.DoesNotContain("old-vm", files);
        Assert.Contains("state/keeper.json", files);
        Assert.Null(_keys.Pinned("old-vm"));        // a reinstalled one is pinned afresh, not "changed"
        Assert.Equal("console: removed machine OLD-VM (old-vm)", OnGitHub("log", "-1", "--format=%s", "fleet-state"));
    }

    [Fact]
    public async Task Forget_IsRepeatable_AndSaysSoWhenThereIsNothingLeft()
    {
        var console = Console();
        Assert.True((await console.ForgetMachineAsync("old-vm", default)).Ok);
        var again = await console.ForgetMachineAsync("old-vm", default);

        Assert.True(again.Ok);
        Assert.Contains("already gone", again.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../main")]
    [InlineData("a/b")]
    [InlineData("old-vm.json; rm -rf")]
    [InlineData("..")]
    public async Task Forget_RefusesIdsThatAreNotPlainMachineIds(string id)
    {
        var before = OnGitHub("rev-parse", "fleet-state");
        var result = await Console().ForgetMachineAsync(id, default);

        Assert.False(result.Ok);
        Assert.Equal(before, OnGitHub("rev-parse", "fleet-state"));   // nothing was touched
    }

    [Fact]
    public async Task Forget_WorksEvenIfAnAgentPushedSinceTheConsoleLastLooked()
    {
        var console = Console();
        // An agent commits a fresh heartbeat after the console's clone last fetched.
        var agents = Path.Combine(_root, "agents");
        File.WriteAllText(Path.Combine(agents, "state", "keeper.json"), "{\"machineId\":\"keeper\",\"hostname\":\"KEEPER\",\"lastSeenUtc\":\"later\"}");
        Git(agents, "add", "-A"); Git(agents, "commit", "-qm", "heartbeat again"); Git(agents, "push", "-q", "origin", "fleet-state");

        Assert.True((await console.ForgetMachineAsync("old-vm", default)).Ok);

        Assert.Contains("later", OnGitHub("show", "fleet-state:state/keeper.json"));   // the agent's newer heartbeat survived
    }

    [Fact]
    public async Task Uninstall_IsASignedCommandThatExpiresWithinTheHour()
    {
        var console = Console();
        Assert.True((await console.SignNowAsync(default)).Ok);   // the console won't build on unsigned control files

        var result = await console.SendCommandAsync("old-vm", "uninstall", default);

        Assert.True(result.Ok, result.Message);
        var cmd = JsonNode.Parse(OnGitHub("show", "main:commands.json"))!["commands"]!["old-vm"]!;
        Assert.Equal("uninstall", cmd["action"]!.GetValue<string>());
        var expires = DateTimeOffset.Parse(cmd["expiresUtc"]!.GetValue<string>());
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(61));
        Assert.Contains("commands.json.sig", OnGitHub("ls-tree", "-r", "--name-only", "main"));
        // The agent would treat a copy of it from yesterday as stale and never act on it.
        Assert.True(OneShotExpiry.IsExpired(cmd["expiresUtc"]!.GetValue<string>(), null, OneShotExpiry.CommandMaxAge, DateTimeOffset.UtcNow.AddHours(2)));
    }

    [Fact]
    public async Task OtherActions_AreStillRefused()
    {
        var console = Console();
        Assert.True((await console.SignNowAsync(default)).Ok);
        Assert.False((await console.SendCommandAsync("old-vm", "format-c", default)).Ok);
    }

    [Fact]
    public void AnUninstallRequestedBeforeTheAgentStarted_IsIgnored_SoAReinstallIsNotRemoved()
    {
        var started = new DateTimeOffset(2026, 10, 29, 22, 33, 0, TimeSpan.Zero);   // the reinstall
        Assert.True(OneShotExpiry.RequestedBefore("2026-10-29T22:28:51.1715570+00:00", started));    // pressed before it: stale
        Assert.False(OneShotExpiry.RequestedBefore("2026-10-29T22:40:00.0000000+00:00", started));   // pressed while it was running: act on it
        Assert.True(OneShotExpiry.RequestedBefore(null, started));                                   // unreadable: fail safe
        Assert.True(OneShotExpiry.RequestedBefore("garbage", started));
    }
}
