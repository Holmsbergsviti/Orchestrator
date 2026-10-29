// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Runs the console's self-update (the "new version" flag and Update button) against REAL
//   git repositories in a temp folder: a bare "GitHub", the operator's clone of the code, and
//   a second clone standing in for whoever pushes new code. Covers the promises that matter:
//   it notices new commits, pulls them without ever losing the operator's own work, and says
//   correctly whether the console must be restarted or the page just reloaded.
// =====================================================================================

using System.Diagnostics;   // running git
using Orchestrator.Console; // ConsoleUpdater
using Xunit;                // the test framework

namespace Orchestrator.Service.Tests;

public sealed class ConsoleUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "console-update-" + Guid.NewGuid().ToString("N"));
    private string Remote => Path.Combine(_root, "github.git");
    private string Mine => Path.Combine(_root, "mine");     // the clone the console runs from
    private string Other => Path.Combine(_root, "other");   // where new code gets pushed from

    public ConsoleUpdaterTests()
    {
        Directory.CreateDirectory(_root);
        Git(_root, "init", "-q", "--bare", "-b", "main", Remote);
        Git(_root, "clone", "-q", Remote, Mine);
        Configure(Mine);
        Write(Mine, "Orchestrator.sln", "sln");
        Write(Mine, "src/Orchestrator.Console/Program.cs", "v1");
        Write(Mine, "src/Orchestrator.Console/wwwroot/index.html", "v1");
        Write(Mine, "docs/CONSOLE.md", "v1");
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

    private static void Write(string repo, string path, string text)
    {
        var full = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>New code lands on GitHub.</summary>
    private void Publish(string message, params (string Path, string Text)[] files)
    {
        Git(Other, "pull", "-q", "--ff-only", "origin", "main");
        foreach (var (path, text) in files) Write(Other, path, text);
        Git(Other, "add", "-A"); Git(Other, "commit", "-qm", message); Git(Other, "push", "-q", "origin", "HEAD:main");
    }

    [Fact]
    public async Task UpToDate_ReportsNothingBehind()
    {
        var status = await new ConsoleUpdater(Mine).StatusAsync(default);
        Assert.True(status.Available);
        Assert.Empty(status.Behind);
        Assert.False(status.RestartNeeded);
    }

    [Fact]
    public async Task NewCommits_AreListedNewestFirst()
    {
        Publish("first", ("docs/CONSOLE.md", "v2"));
        Publish("second", ("docs/CONSOLE.md", "v3"));
        var status = await new ConsoleUpdater(Mine).StatusAsync(default);
        Assert.Equal(new[] { "second", "first" }, status.Behind);
    }

    [Fact]
    public async Task NotAClone_SaysSoInsteadOfOfferingAnUpdate()
    {
        var u = new ConsoleUpdater((string?)null);
        var status = await u.StatusAsync(default);
        Assert.False(status.Available);
        Assert.NotNull(status.Reason);
        Assert.False((await u.UpdateAsync(default)).Ok);
    }

    [Fact]
    public async Task Update_PullsAndPageOnlyChange_NeedsAReloadNotARestart()
    {
        Publish("restyle", ("src/Orchestrator.Console/wwwroot/index.html", "v2"));
        var result = await new ConsoleUpdater(Mine).UpdateAsync(default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { "restyle" }, result.Pulled);
        Assert.True(result.ReloadNeeded);
        Assert.False(result.RestartNeeded);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(Mine, "src/Orchestrator.Console/wwwroot/index.html")));
    }

    [Fact]
    public async Task Update_ChangeToConsoleCode_NeedsARestart_AndStaysFlaggedUntilOne()
    {
        var running = new ConsoleUpdater(Mine);   // "the process that is running now"
        Publish("new endpoint", ("src/Orchestrator.Console/Program.cs", "v2"));

        var result = await running.UpdateAsync(default);
        Assert.True(result.Ok, result.Message);
        Assert.True(result.RestartNeeded);

        // Even a page reload / the next poll keeps saying so: the process still runs the old code...
        var after = await running.StatusAsync(default);
        Assert.Empty(after.Behind);
        Assert.True(after.RestartNeeded);

        // ...and a freshly started console (new process, same clone) is, of course, fine.
        Assert.False((await new ConsoleUpdater(Mine).StatusAsync(default)).RestartNeeded);
    }

    [Fact]
    public async Task Update_ChangeOutsideTheConsole_NeedsNeitherRestartNorReload()
    {
        Publish("docs only", ("docs/CONSOLE.md", "v2"));
        var result = await new ConsoleUpdater(Mine).UpdateAsync(default);
        Assert.True(result.Ok, result.Message);
        Assert.False(result.RestartNeeded);
        Assert.False(result.ReloadNeeded);
    }

    [Fact]
    public async Task UncommittedChanges_AreNeverTouched()
    {
        Publish("restyle", ("src/Orchestrator.Console/wwwroot/index.html", "v2"));
        Write(Mine, "docs/CONSOLE.md", "my edit");

        var result = await new ConsoleUpdater(Mine).UpdateAsync(default);

        Assert.False(result.Ok);
        Assert.Contains("uncommitted", result.Message);
        Assert.Equal("my edit", File.ReadAllText(Path.Combine(Mine, "docs/CONSOLE.md")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(Mine, "src/Orchestrator.Console/wwwroot/index.html")));
    }

    [Fact]
    public async Task LocalCommitsNotOnGitHub_AreNeverMoved()
    {
        Write(Mine, "notes.txt", "mine");
        Git(Mine, "add", "-A"); Git(Mine, "commit", "-qm", "local only");
        Publish("upstream", ("docs/CONSOLE.md", "v2"));

        var result = await new ConsoleUpdater(Mine).UpdateAsync(default);

        Assert.False(result.Ok);
        Assert.Contains("git pull --rebase origin main", result.Message);
        Assert.Equal("local only", Git(Mine, "log", "--format=%s", "-1"));
    }

    [Fact]
    public async Task OnAnotherBranchWithNothingOfYours_SwitchesBackToMainAndUpdates()
    {
        Git(Mine, "checkout", "-q", "-b", "scratch");   // same commit as main, nothing unique on it
        Publish("upstream", ("docs/CONSOLE.md", "v2"));

        var result = await new ConsoleUpdater(Mine).UpdateAsync(default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("main", Git(Mine, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(Mine, "docs/CONSOLE.md")));
    }

    [Fact]
    public void FindCodeRepo_WalksUpToTheCloneThatHoldsTheSolution()
    {
        var deep = Path.Combine(Mine, "src", "Orchestrator.Console", "bin");
        Directory.CreateDirectory(deep);
        Assert.Equal(Mine, ConsoleUpdater.FindCodeRepo(deep));
        Assert.Null(ConsoleUpdater.FindCodeRepo(_root));
    }
}
