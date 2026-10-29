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

/// <summary>The push step failed after a local commit was made — usually because GitHub moved
/// on while we were editing (CI publishes agent.json to the same repo on every agent build).
/// <see cref="GitRepo.CommitAndPushAsync"/> has already undone the local commit when this is
/// thrown, so retrying from scratch is safe. A separate type from the generic GitException so
/// callers can retry exactly this case and nothing else.</summary>
public sealed class PushRejectedException(string message) : Exception(message);

/// <summary>Runs git commands against a single local repository directory.</summary>
public sealed class GitRepo(string repoPath)
{
    public string RepoPath { get; } = repoPath;

    /// <summary>True if the path looks like a git working tree.</summary>
    public bool IsValid()
        => Directory.Exists(RepoPath) && TryRun(out _, out _, "rev-parse", "--is-inside-work-tree");

    /// <summary>"author, relative time" of the last commit on <paramref name="refSpec"/> that
    /// touched <paramref name="path"/>, or null.</summary>
    public string? LastChange(string refSpec, string path)
        => TryRun(out var o, out _, "log", "-1", "--format=%an, %ar", refSpec, "--", path) && !string.IsNullOrWhiteSpace(o) ? o.Trim() : null;

    /// <summary>Download the latest refs from the remote (no working-tree changes).</summary>
    public Task FetchAsync(string remote, CancellationToken ct)
        => RunAsync(ct, "fetch", remote, "--prune", "--quiet");

    /// <summary>Read a file's content from a specific ref (e.g. "origin/main:manifest.json"). Null if it doesn't exist.</summary>
    public string? ReadFileFromRef(string refSpec, string path)
        => TryRun(out var stdout, out _, "show", $"{refSpec}:{path}") ? stdout : null;

    /// <summary>Read a file's raw bytes from a specific ref (for binary content like a screenshot
    /// JPEG). Reads the process's stdout stream directly so no text encoding can corrupt the bytes.
    /// Null if the file doesn't exist on that ref.</summary>
    public byte[]? ReadFileBytesFromRef(string refSpec, string path)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("show");
        psi.ArgumentList.Add($"{refSpec}:{path}");

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        // Read stdout (raw bytes, bypassing the TextReader) and stderr concurrently — reading
        // them one after the other risks a pipe-buffer deadlock once the image gets large.
        using var ms = new MemoryStream();
        var outTask = proc.StandardOutput.BaseStream.CopyToAsync(ms);
        var errTask = proc.StandardError.ReadToEndAsync();
        Task.WaitAll(outTask, errTask);
        proc.WaitForExit();
        return proc.ExitCode == 0 ? ms.ToArray() : null;
    }

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

    /// <summary>The branch checked out now, or null when HEAD is detached.</summary>
    public string? CurrentBranch()
        => TryRun(out var o, out _, "symbolic-ref", "--short", "-q", "HEAD") && !string.IsNullOrWhiteSpace(o) ? o.Trim() : null;

    /// <summary>Subjects of the commits on <paramref name="theirs"/> that <paramref name="mine"/> doesn't have,
    /// newest first ("what would I get by updating?").</summary>
    public IReadOnlyList<string> CommitsBehind(string mine, string theirs)
        => TryRun(out var o, out _, "log", "--format=%s", $"{mine}..{theirs}")
            ? o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    /// <summary>True when <paramref name="mine"/> has commits that <paramref name="theirs"/> doesn't.</summary>
    public bool HasCommitsNotIn(string mine, string theirs)
        => TryRun(out var o, out _, "rev-list", "--count", $"{theirs}..{mine}") && int.TryParse(o.Trim(), out var n) && n > 0;

    /// <summary>Paths that differ between two revisions.</summary>
    public IReadOnlyList<string> ChangedPaths(string from, string to)
        => TryRun(out var o, out _, "diff", "--name-only", from, to)
            ? o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    /// <summary>Does <paramref name="branch"/> exist on the remote? Asks the remote itself, so a stale
    /// local copy of the ref can't answer for it. Throws if the remote can't be reached.</summary>
    public async Task<bool> RemoteBranchExistsAsync(string remote, string branch, CancellationToken ct)
    {
        var (code, _, err) = await RunCore(new[] { "ls-remote", "--exit-code", "--heads", remote, branch }, ct);
        if (code == 0) return true;
        if (code == 2) return false;   // reached the remote; no such branch
        throw new GitException($"git ls-remote {remote} failed: {err.Trim()}");
    }

    /// <summary>Delete a branch on the remote. Already gone is fine.</summary>
    public async Task DeleteRemoteBranchAsync(string remote, string branch, CancellationToken ct)
    {
        var (code, o, err) = await RunCore(new[] { "push", "--quiet", remote, "--delete", branch }, ct);
        if (code != 0 && !(err + o).Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase))
            throw new GitException($"git push {remote} --delete {branch} failed: {(string.IsNullOrWhiteSpace(err) ? o : err).Trim()}");
        // Drop our own copy of the ref too, or a stale origin/<branch> would still answer reads.
        TryRun(out _, out _, "update-ref", "-d", $"refs/remotes/{remote}/{branch}");
    }

    /// <summary>Replace <paramref name="branch"/> on the remote with ONE parentless commit holding just
    /// <paramref name="files"/>: no history is kept on that branch, and the clone's own branches and working
    /// tree are never touched. The commit is built in a scratch repository and pushed with the clone's
    /// remote URL, so it uses the same git credentials as every other push.</summary>
    public async Task PublishOrphanBranchAsync(string remote, string branch, IReadOnlyDictionary<string, byte[]> files, string message, CancellationToken ct)
    {
        var url = (await RunAsync(ct, "remote", "get-url", remote)).Trim();
        var scratchDir = Path.Combine(Path.GetTempPath(), "orch-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);
        try
        {
            var scratch = new GitRepo(scratchDir);
            await scratch.RunAsync(ct, "init", "--quiet");
            foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(scratchDir, name), bytes);
            await scratch.RunAsync(ct, "add", "-A");
            await scratch.RunAsync(ct, "-c", "user.name=Orchestrator console", "-c", "user.email=console@orchestrator.invalid",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message);
            await scratch.RunAsync(ct, "push", "--quiet", "--force", url, $"HEAD:refs/heads/{branch}");
        }
        finally { try { Directory.Delete(scratchDir, recursive: true); } catch { /* temp cleanup */ } }
    }

    /// <summary>Remove files/folders from <paramref name="branch"/> on the remote with one ordinary
    /// commit, without that branch ever being checked out in this clone (it is worked on in a
    /// scratch clone and pushed with the same git credentials). Returns false when there was
    /// nothing to remove or the branch doesn't exist. A push that loses a race (agents commit to the
    /// fleet-state branch constantly) starts over from a fresh clone, a few times.</summary>
    public async Task<bool> RemovePathsOnRemoteBranchAsync(string remote, string branch, IEnumerable<string> paths, string message, CancellationToken ct)
    {
        var url = (await RunAsync(ct, "remote", "get-url", remote)).Trim();
        var targets = paths.ToList();
        for (var attempt = 1; ; attempt++)
        {
            var scratchDir = Path.Combine(Path.GetTempPath(), "orch-remove-" + Guid.NewGuid().ToString("N"));
            try
            {
                try
                {
                    await new GitRepo(Path.GetTempPath()).RunAsync(ct, "clone", "--quiet", "--single-branch", "--branch", branch, "--depth", "1", url, scratchDir);
                }
                catch (GitException ex) when (ex.Message.Contains("not found in upstream", StringComparison.OrdinalIgnoreCase)
                                              || ex.Message.Contains("Remote branch", StringComparison.OrdinalIgnoreCase))
                {
                    return false;   // no such branch: nothing to remove
                }

                var scratch = new GitRepo(scratchDir);
                await scratch.RunAsync(ct, new[] { "rm", "-r", "-q", "--ignore-unmatch", "--" }.Concat(targets).ToArray());
                if (string.IsNullOrWhiteSpace(await scratch.RunAsync(ct, "status", "--porcelain"))) return false;
                await scratch.RunAsync(ct, "-c", "user.name=Orchestrator console", "-c", "user.email=console@orchestrator.invalid",
                    "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message);
                try
                {
                    await scratch.RunAsync(ct, "push", "--quiet", "origin", $"HEAD:refs/heads/{branch}");
                    return true;
                }
                catch (GitException ex) when (attempt < 4 && (ex.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                                                              || ex.Message.Contains("fetch first", StringComparison.OrdinalIgnoreCase)))
                {
                    // Someone (an agent) pushed in between: go round again from what is there now.
                }
            }
            finally { try { Directory.Delete(scratchDir, recursive: true); } catch { /* temp cleanup */ } }
        }
    }

    /// <summary>The full SHA HEAD points at now, or null when it can't be read (synchronous twin of <see cref="HeadAsync"/>).</summary>
    public string? Head() => TryRun(out var o, out _, "rev-parse", "HEAD") ? o.Trim() : null;

    /// <summary>The full SHA HEAD points at now.</summary>
    public async Task<string> HeadAsync(CancellationToken ct) => (await RunAsync(ct, "rev-parse", "HEAD")).Trim();

    /// <summary>Put the working tree back exactly as it was at <paramref name="sha"/>: drops any
    /// commit made since, restores edited files, and deletes the given paths if they were newly
    /// created (e.g. a program file copied in, or a fresh .sig). Only safe because every console
    /// save starts from a clean tree.</summary>
    public async Task RollbackAsync(string sha, IEnumerable<string> paths)
    {
        await RunAsync(CancellationToken.None, "reset", "--hard", sha);
        foreach (var p in paths)
            TryRun(out _, out _, "clean", "-fdq", "--", p);   // best effort: a path that never existed is fine
    }

    /// <summary>Stage the given paths, commit, and push. Returns the new commit's short SHA.
    /// If ANY step fails, the clone is put back exactly as it was before: a commit that never
    /// reached GitHub would otherwise sit on local main, and every later save would then be
    /// refused as "diverged" until someone repaired the clone by hand. A failed push is
    /// reported as <see cref="PushRejectedException"/> so the caller can retry once.</summary>
    public async Task<string> CommitAndPushAsync(string remote, string branch, string message, IEnumerable<string> paths, CancellationToken ct)
    {
        var all = paths.ToList();
        var start = await HeadAsync(ct);
        try
        {
            foreach (var p in all) await RunAsync(ct, "add", "--", p);
            await RunAsync(ct, "commit", "-m", message);
            var sha = (await RunAsync(ct, "rev-parse", "--short", "HEAD")).Trim();
            try { await RunAsync(ct, "push", remote, branch); }
            catch (GitException ex) { throw new PushRejectedException(ex.Message); }
            return sha;
        }
        catch (Exception)
        {
            await RollbackAsync(start, all);
            throw;
        }
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
