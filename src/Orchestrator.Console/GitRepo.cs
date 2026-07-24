// =====================================================================================
// FILE PURPOSE (in plain terms):
//   A thin wrapper around the "git" command for one local clone of your control repo.
//   The console uses this to fetch the latest from GitHub, read files straight out of a
//   branch (without switching the working tree), and commit + push your edits. It shells
//   out to the git you already have installed, so it uses your existing GitHub login.
// =====================================================================================

using System.Diagnostics;   // to launch the git process

namespace Orchestrator.Console;

/// <summary>Raised when a git command exits non-zero; carries stderr for the UI.</summary>
public sealed class GitException(string message) : Exception(message);

/// <summary>Runs git commands against a single local repository directory.</summary>
public sealed class GitRepo(string repoPath)
{
    public string RepoPath { get; } = repoPath;

    /// <summary>True if the path looks like a git working tree.</summary>
    public bool IsValid()
        => Directory.Exists(RepoPath) && TryRun(out _, out _, "rev-parse", "--is-inside-work-tree");

    /// <summary>Download the latest refs from the remote (no working-tree changes).</summary>
    public Task FetchAsync(string remote, CancellationToken ct)
        => RunAsync(ct, "fetch", remote, "--prune", "--quiet");

    /// <summary>Read a file's content from a specific ref (e.g. "origin/main:manifest.json"). Null if it doesn't exist.</summary>
    public string? ReadFileFromRef(string refSpec, string path)
        => TryRun(out var stdout, out _, "show", $"{refSpec}:{path}") ? stdout : null;

    /// <summary>List file names directly under a directory on a ref (e.g. state/*.json on origin/fleet-state).</summary>
    public IReadOnlyList<string> ListDirOnRef(string refSpec, string dir)
    {
        if (!TryRun(out var stdout, out _, "ls-tree", "--name-only", refSpec, dir.EndsWith('/') ? dir : dir + "/"))
            return Array.Empty<string>();   // dir/ref missing -> empty
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>True if the working tree has no uncommitted changes.</summary>
    public bool IsClean()
        => TryRun(out var stdout, out _, "status", "--porcelain") && string.IsNullOrWhiteSpace(stdout);

    /// <summary>Check out the branch and fast-forward it to the remote (fails on divergence).</summary>
    public async Task SyncBranchToRemoteAsync(string remote, string branch, CancellationToken ct)
    {
        await RunAsync(ct, "checkout", branch);
        await RunAsync(ct, "merge", "--ff-only", $"{remote}/{branch}");
    }

    /// <summary>Stage the given paths, commit, and push. Returns the new commit's short SHA.</summary>
    public async Task<string> CommitAndPushAsync(string remote, string branch, string message, IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (var p in paths) await RunAsync(ct, "add", "--", p);
        await RunAsync(ct, "commit", "-m", message);
        var sha = (await RunAsync(ct, "rev-parse", "--short", "HEAD")).Trim();
        await RunAsync(ct, "push", remote, branch);
        return sha;
    }

    // ---- process plumbing --------------------------------------------------------------

    private bool TryRun(out string stdout, out string stderr, params string[] args)
    {
        var (code, o, e) = RunCore(args, CancellationToken.None).GetAwaiter().GetResult();
        stdout = o; stderr = e;
        return code == 0;
    }

    private async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var (code, stdout, stderr) = await RunCore(args, ct);
        if (code != 0)
            throw new GitException($"git {string.Join(' ', args)} failed: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
        return stdout;
    }

    private async Task<(int Code, string Out, string Err)> RunCore(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);   // ArgumentList avoids manual quoting/escaping

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await outTask, await errTask);
    }
}
