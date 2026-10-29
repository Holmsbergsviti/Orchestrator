// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The console's "Update" button. The console runs straight from a clone of the code repo,
//   so updating it is: fetch, check there's nothing of yours in the way, fast-forward to the
//   newest main, and say what that means — a restart is needed when the console's own code
//   changed, a page reload is enough when only the web page did.
//
//   It is deliberately cautious, because it touches the operator's own checkout:
//     - never discards or merges anything: a clone with uncommitted changes, or commits that
//       never reached GitHub, is left alone and told how to sort it out;
//     - only fast-forwards, and only to origin/main;
//     - switching to main is done only when the tree is clean.
//   It also remembers which commit the running process started from, so "restart needed" stays
//   true until the console really is restarted — even if the page is reloaded, or the code was
//   pulled by hand with git.
//   It never touches the CONTROL repo — the console already fetches that on every load, and
//   the agents update themselves from the published build.
// =====================================================================================

namespace Orchestrator.Console;

public sealed class UpdateStatus
{
    /// <summary>False when the console isn't running from a git clone (e.g. a published copy).</summary>
    public bool Available { get; set; }
    public string? Reason { get; set; }
    public string? Branch { get; set; }
    public List<string> Behind { get; set; } = new();
    /// <summary>The code on disk has moved past what this running process loaded: it must be restarted.</summary>
    public bool RestartNeeded { get; set; }
}

public sealed class UpdateResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Pulled { get; set; } = new();
    /// <summary>The console's own code changed: stop it and start it again to run the new one.</summary>
    public bool RestartNeeded { get; set; }
    /// <summary>Only the web page changed: reloading the browser tab is enough.</summary>
    public bool ReloadNeeded { get; set; }
}

public sealed class ConsoleUpdater
{
    private const string Remote = "origin";
    private const string Branch = "main";

    private readonly string? _repoPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _startHead;   // the commit this process was started from

    public ConsoleUpdater(ConsoleOptions opt)
        : this(string.IsNullOrWhiteSpace(opt.CodeRepoPath) ? FindCodeRepo(AppContext.BaseDirectory) : opt.CodeRepoPath) { }

    /// <summary>Test seam: an explicit clone (or null for "not running from a clone").</summary>
    public ConsoleUpdater(string? codeRepoPath)
    {
        _repoPath = codeRepoPath;
        _startHead = codeRepoPath is null ? null : new GitRepo(codeRepoPath).Head();
    }

    /// <summary>Does this set of changed files affect the running console process (as opposed to the
    /// web page, which is served from disk and only needs a reload, or other parts of the repo)?</summary>
    private static bool NeedsRestart(IEnumerable<string> changed)
        => changed.Any(p => (p.StartsWith("src/Orchestrator.Console/", StringComparison.Ordinal)
                             && !p.StartsWith("src/Orchestrator.Console/wwwroot/", StringComparison.Ordinal))
                            || p is "Orchestrator.sln" or "defaults.json");

    /// <summary>Walk up from where the console is running until a git clone that holds the
    /// solution file is found — i.e. the code repo it was started from.</summary>
    public static string? FindCodeRepo(string startDir)
    {
        for (var d = new DirectoryInfo(startDir); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) && File.Exists(Path.Combine(d.FullName, "Orchestrator.sln")))
                return d.FullName;
        return null;
    }

    public async Task<UpdateStatus> StatusAsync(CancellationToken ct)
    {
        if (_repoPath is null)
            return new UpdateStatus { Reason = "The console isn't running from a git clone of the code, so it can't update itself." };
        var git = new GitRepo(_repoPath);
        try { await git.FetchAsync(Remote, ct); }
        catch (GitException ex) { return new UpdateStatus { Reason = $"Couldn't reach GitHub: {ex.Message.Split('\n')[0]}" }; }
        return new UpdateStatus
        {
            Available = true,
            Branch = git.CurrentBranch(),
            Behind = git.CommitsBehind("HEAD", $"{Remote}/{Branch}").ToList(),
            RestartNeeded = _startHead is not null && git.Head() is { } now && now != _startHead
                            && NeedsRestart(git.ChangedPaths(_startHead, now))
        };
    }

    public async Task<UpdateResult> UpdateAsync(CancellationToken ct)
    {
        if (_repoPath is null)
            return Fail("The console isn't running from a git clone of the code, so it can't update itself.");
        await _gate.WaitAsync(ct);
        try
        {
            var git = new GitRepo(_repoPath);
            try { await git.FetchAsync(Remote, ct); }
            catch (GitException ex) { return Fail($"Couldn't reach GitHub: {ex.Message.Split('\n')[0]}"); }

            if (!git.IsClean())
                return Fail($"{_repoPath} has uncommitted changes, so I won't touch it. Commit or discard them first (git status shows what).");

            var theirs = $"{Remote}/{Branch}";
            if (git.HasCommitsNotIn("HEAD", theirs))
                return Fail($"{_repoPath} has commits that aren't on GitHub's {Branch}" +
                            (git.CurrentBranch() is { } b && b != Branch ? $" (you're on '{b}')" : "") +
                            $", so I won't move it. If you don't need them: git checkout {Branch} && git pull. Otherwise: git pull --rebase {Remote} {Branch}.");

            var before = await git.HeadAsync(ct);
            var pulled = git.CommitsBehind("HEAD", theirs).ToList();
            try { await git.SyncBranchToRemoteAsync(Remote, Branch, ct); }   // checkout main (clean tree), ff-only
            catch (GitException ex) { return Fail($"Couldn't fast-forward: {ex.Message.Split('\n')[0]}"); }

            var changed = git.ChangedPaths(before, await git.HeadAsync(ct));
            var restart = NeedsRestart(changed);
            var reload = changed.Any(p => p.StartsWith("src/Orchestrator.Console/wwwroot/", StringComparison.Ordinal));
            return new UpdateResult
            {
                Ok = true,
                Pulled = pulled,
                RestartNeeded = restart,
                ReloadNeeded = reload && !restart,
                Message = pulled.Count == 0 && changed.Count == 0
                    ? "Already up to date."
                    : restart ? "Updated. Restart the console (Ctrl+C, then start it again) to run the new version."
                    : reload ? "Updated. Reload this page to get the new look."
                    : "Updated. Nothing to restart — the changes don't affect the console."
            };
        }
        finally { _gate.Release(); }
    }

    private static UpdateResult Fail(string message) => new() { Ok = false, Message = message };
}
