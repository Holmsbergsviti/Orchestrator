// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Decides whether a program's install folder is safe to delete. Uninstalling a program
//   deletes its whole installPath, recursively, as SYSTEM — and that path comes straight
//   from the manifest. A typo ("C:\Program Files"), a folder two programs share, or a
//   program that was simply un-targeted from this machine would otherwise wipe whatever
//   lives there. This check refuses drive roots, Windows/system/user-profile folders
//   (and anything that contains them), the agent's own folders, and any folder that still
//   holds another program's files.
//   Pure string logic on Windows-style paths, so it can be unit-tested on a Mac/Linux.
// =====================================================================================

namespace Orchestrator.Service.Services;   // groups this with the other services

public static class InstallPathGuard   // static = pure helper, no instance/state
{
    // Folders (relative to the path's own drive) that must never be deleted, nor any folder
    // that contains them. "C:\Windows\Orch\programs\x" is fine: it's INSIDE C:\Windows, not
    // C:\Windows itself or one of its parents.
    private static readonly string[] ProtectedFolders =
    {
        @"Windows",
        @"Windows\System32",
        @"Windows\SysWOW64",
        @"Program Files",
        @"Program Files (x86)",
        @"ProgramData",
        @"Users",
        @"Users\Public",
        @"Users\Default"
    };

    /// <summary>
    /// Returns null when <paramref name="installPath"/> may be deleted recursively, or a
    /// human-readable reason when it must not be.
    /// </summary>
    /// <param name="installPath">The program's installPath from the manifest.</param>
    /// <param name="agentRoot">The agent's own install root (e.g. C:\Windows\Orch).</param>
    /// <param name="pathsStillInUse">installPaths of programs that remain active on this machine.</param>
    public static string? WhyUnsafeToDelete(string? installPath, string agentRoot, IEnumerable<string?> pathsStillInUse)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return "installPath is empty";   // nothing sensible to delete

        var path = Normalize(installPath);   // canonical form: backslashes, no trailing slash
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return "it is a network (UNC) path";                                       // never recurse into a share
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return "it is not an absolute path with a drive letter";                   // relative paths resolve unpredictably

        var segments = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);   // the folders below the drive
        if (segments.Length == 0) return "it is the root of a drive";                  // e.g. C:\
        if (segments.Any(s => s is "." or ".."))
            return "it contains '.' or '..' segments";                                 // could point anywhere

        var drive = path[..3];   // e.g. "C:\" — protected folders are checked on the path's own drive
        foreach (var rel in ProtectedFolders)
        {
            if (ContainsOrEquals(path, drive + rel))
                return $"it is, or contains, the system folder {drive + rel}";
        }

        // A user's profile folder (C:\Users\bob) or anything directly in it (Desktop, AppData...).
        if (segments.Length <= 3 && segments[0].Equals("Users", StringComparison.OrdinalIgnoreCase))
            return "it is a user profile folder or one of its top-level folders";

        if (!string.IsNullOrWhiteSpace(agentRoot))
        {
            var root = Normalize(agentRoot);
            if (ContainsOrEquals(path, root))
                return $"it is, or contains, the agent's own folder {root}";
            var programsRoot = root + @"\programs";
            if (ContainsOrEquals(path, programsRoot))
                return $"it is, or contains, the shared programs folder {programsRoot}";
        }

        foreach (var other in pathsStillInUse)
        {
            if (string.IsNullOrWhiteSpace(other)) continue;
            var o = Normalize(other);
            if (ContainsOrEquals(path, o))
                return $"another active program is still installed in {o}";            // shared folder: keep its files
        }

        return null;   // passed every check
    }

    /// <summary>True when <paramref name="folder"/> is <paramref name="candidate"/> itself or one of its parents.</summary>
    private static bool ContainsOrEquals(string folder, string candidate)
    {
        candidate = Normalize(candidate);
        return candidate.Equals(folder, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trim, use backslashes, and drop trailing separators (but keep "C:\").</summary>
    private static string Normalize(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        while (p.Length > 3 && p.EndsWith('\\')) p = p[..^1];   // "C:\foo\" -> "C:\foo"
        if (p.Length == 2 && p[1] == ':') p += '\\';             // "C:" -> "C:\"
        return p;
    }
}
