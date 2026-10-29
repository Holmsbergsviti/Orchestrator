// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Keeps install.ps1 and update-agent.ps1 in the install folder in step with the running
//   agent build.
//
//   Self-update downloads only the exe, then runs the install.ps1 that is ALREADY on the
//   machine. That script rewrites appsettings.json from scratch, keeping only the settings it
//   knows about. So without this, a machine keeps the install.ps1 from its first install
//   forever, and every self-update quietly wipes any setting added since (that is how RelayUrl
//   went missing fleet-wide once).
//
//   The fix: both scripts are embedded in the exe (whose hash self-update already verified),
//   and on startup the service writes them out whenever the copy on disk differs.
// =====================================================================================

using System.Reflection;             // embedded resources
using Microsoft.Extensions.Logging;  // logging
using Orchestrator.Service.Models;   // OrchestratorConfig

namespace Orchestrator.Service.Services;

public static class InstalledScripts
{
    private static readonly string[] Scripts = { "install.ps1", "update-agent.ps1" };

    /// <summary>Write the embedded scripts into the install folder where they differ. Only when
    /// this exe IS the installed one, so running a dev build from a source tree never touches a
    /// real install. Best-effort: a failure here must not stop the service.</summary>
    public static void Refresh(OrchestratorConfig config, ILogger log)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(exeDir) || !SamePath(exeDir, config.RootPath)) return;

        foreach (var name in Scripts)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("scripts/" + name);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var embedded = ms.ToArray();

                var target = Path.Combine(config.RootPath, name);
                if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(embedded)) continue;

                var tmp = target + ".tmp";
                File.WriteAllBytes(tmp, embedded);
                File.Move(tmp, target, overwrite: true);   // never leave a half-written script for self-update to run
                log.LogInformation("Refreshed {Script} in the install folder to match this build", name);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not refresh {Script} in the install folder", name);
            }
        }
    }

    /// <summary>Copy an embedded script to a fresh temp file and return its path (null if there is no
    /// such script). Used for scripts that must run from OUTSIDE the install folder, because they
    /// delete it.</summary>
    public static string? ExtractToTemp(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("scripts/" + name);
        if (stream is null) return null;
        var path = Path.Combine(Path.GetTempPath(), $"orch-{Path.GetFileNameWithoutExtension(name)}-{Guid.NewGuid():N}.ps1");
        using (var file = File.Create(path)) stream.CopyTo(file);
        return path;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
