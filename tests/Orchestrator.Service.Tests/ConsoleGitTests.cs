// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Runs the console's git handling against REAL git repositories in a temp folder: a bare
//   "GitHub", the operator's clone, and a second clone standing in for CI or another machine
//   pushing in the meantime. Covers what went wrong in practice: a push that GitHub rejects
//   because the repo moved (CI publishes agent.json there on every agent build) used to leave
//   a commit stranded on local main, after which every save was refused as "diverged".
//   Now a failed save puts the clone back, retries once on a fresh fetch, and two writes at
//   once take turns instead of colliding.
// =====================================================================================

using System.Diagnostics;                          // running git
using System.Text.Json.Nodes;                      // reading commands.json back
using Microsoft.Extensions.Logging.Abstractions;   // NullLogger
using Orchestrator.Console;                        // ControlRepo / GitRepo
using Xunit;                                       // the test framework

namespace Orchestrator.Service.Tests;

public sealed class ConsoleGitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "console-git-" + Guid.NewGuid().ToString("N"));
    private string Remote => Path.Combine(_root, "github.git");
    private string Mine => Path.Combine(_root, "mine");      // the operator's clone, the one the console drives
    private string Other => Path.Combine(_root, "other");    // CI / another machine pushing to the same repo

    public ConsoleGitTests()
    {
        Directory.CreateDirectory(_root);
        Git(_root, "init", "-q", "--bare", "-b", "main", Remote);
        Git(_root, "clone", "-q", Remote, Mine);
        Configure(Mine);
        File.WriteAllText(Path.Combine(Mine, "commands.json"), "{}");
        Git(Mine, "add", "-A"); Git(Mine, "commit", "-qm", "init"); Git(Mine, "push", "-q", "origin", "HEAD:main");
        Git(_root, "clone", "-q", Remote, Other);
        Configure(Other);
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

    /// <summary>CI (or another machine) lands a commit on GitHub.</summary>
    private void RemoteMovesOn(string file = "agent.json")
    {
        Git(Other, "pull", "-q", "--ff-only", "origin", "main");
        File.WriteAllText(Path.Combine(Other, file), Guid.NewGuid().ToString());
        Git(Other, "add", "-A"); Git(Other, "commit", "-qm", "ci: " + file); Git(Other, "push", "-q", "origin", "HEAD:main");
    }

    /// <summary>A pre-push hook that makes GitHub move on right before the first <paramref name="times"/>
    /// pushes go out — i.e. after the console has fetched and edited, which is the race that matters.</summary>
    private void InstallRacingHook(int times)
    {
        var hook = Path.Combine(Mine, ".git", "hooks", "pre-push");
        var counter = Path.Combine(_root, "pushes");
        File.WriteAllText(counter, "0");
        File.WriteAllText(hook, $@"#!/bin/sh
n=$(cat '{counter}'); n=$((n+1)); echo $n > '{counter}'
if [ $n -le {times} ]; then
  cd '{Other}' && git pull -q --ff-only origin main && echo $n-$(date +%s%N) > ci-$n.txt \
    && git add -A && git commit -qm ci-$n && git push -q origin HEAD:main
fi
exit 0
");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private ControlRepo Console() => new(
        new ConsoleOptions { ControlRepoPath = Mine, Remote = "origin", MainBranch = "main", SigningKeyPath = Path.Combine(_root, "no-key.pem") },
        new RelayHub(NullLogger<RelayHub>.Instance),
        new RelayKeyStore(Path.Combine(_root, "pins.json"), NullLogger<RelayKeyStore>.Instance),
        new ControlSigning(Path.Combine(_root, "no-key.pem")),
        NullLogger<ControlRepo>.Instance);

    private string OnGitHub(string file) { Git(_root, "--git-dir=" + Remote, "rev-parse", "main"); return Git(_root, "--git-dir=" + Remote, "show", "main:" + file); }

    [Fact]
    public async Task RejectedPush_PutsTheCloneBack_SoNothingIsStranded()
    {
        var git = new GitRepo(Mine);
        var start = await git.HeadAsync(default);
        RemoteMovesOn();   // GitHub is now ahead of our clone
        File.WriteAllText(Path.Combine(Mine, "commands.json"), "{\"edited\":true}");
        File.WriteAllText(Path.Combine(Mine, "brand-new.txt"), "x");

        await Assert.ThrowsAsync<PushRejectedException>(() =>
            git.CommitAndPushAsync("origin", "main", "edit", new[] { "commands.json", "brand-new.txt" }, default));

        Assert.Equal(start, await git.HeadAsync(default));   // the failed commit is gone
        Assert.True(git.IsClean());                          // edited file restored, new file removed
        Assert.False(File.Exists(Path.Combine(Mine, "brand-new.txt")));
        await git.FetchAsync("origin", default);
        await git.SyncBranchToRemoteAsync("origin", "main", default);   // and the next save can fast-forward, which is the point
    }

    [Fact]
    public async Task SaveRacingWithCi_RetriesOnceAndLandsOnTopOfIt()
    {
        InstallRacingHook(times: 1);
        var result = await Console().SendCommandAsync("m1", "restart", default);

        Assert.True(result.Ok, result.Message);
        var cmds = JsonNode.Parse(OnGitHub("commands.json"))!["commands"]!.AsObject();
        Assert.True(cmds.ContainsKey("m1"));                       // our change is on GitHub...
        Assert.Contains("ci-1", Git(_root, "--git-dir=" + Remote, "log", "--format=%s", "main"));   // ...on top of CI's, not instead of it
        Assert.True(new GitRepo(Mine).IsClean());
    }

    [Fact]
    public async Task SaveThatKeepsLosingTheRace_ReportsItAndLeavesTheCloneUsable()
    {
        InstallRacingHook(times: 5);
        var console = Console();
        var result = await console.SendCommandAsync("m1", "restart", default);

        Assert.False(result.Ok);
        Assert.Contains("try again", result.Message);
        Assert.True(new GitRepo(Mine).IsClean());
        Assert.DoesNotContain("m1", OnGitHub("commands.json"));    // nothing half-applied

        // Once the race stops, the very next save works: no manual repair needed.
        File.Delete(Path.Combine(Mine, ".git", "hooks", "pre-push"));
        Assert.True((await console.SendCommandAsync("m1", "restart", default)).Ok);
    }

    [Fact]
    public async Task TwoWritesAtOnce_TakeTurnsAndBothLand()
    {
        var console = Console();
        var results = await Task.WhenAll(
            console.SendCommandAsync("m1", "restart", default),
            console.SendCommandAsync("m2", "shutdown", default));

        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        var cmds = JsonNode.Parse(OnGitHub("commands.json"))!["commands"]!.AsObject();
        Assert.True(cmds.ContainsKey("m1") && cmds.ContainsKey("m2"));
    }

    [Fact]
    public async Task LocalCommitNeverPushedPlusNewerGitHub_GivesTheCommandThatFixesIt()
    {
        // Exactly what happened on the operator's Mac: an own commit that never reached GitHub,
        // and CI publishing on top of the old tip.
        File.WriteAllText(Path.Combine(Mine, "notes.txt"), "mine");
        Git(Mine, "add", "-A"); Git(Mine, "commit", "-qm", "local only");
        RemoteMovesOn();

        var result = await Console().SendCommandAsync("m1", "restart", default);

        Assert.False(result.Ok);
        Assert.Contains("git pull --rebase origin main", result.Message);
        Assert.Contains("local only", Git(Mine, "log", "--format=%s", "-1"));   // the user's own commit is untouched
    }
}
