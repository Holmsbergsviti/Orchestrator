using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Orchestrator.Service.Services;

/// <summary>
/// Turns the service exe into its own installer. <c>orchestrator-service.exe install</c>
/// copies itself to the install root, writes appsettings.json, locks the folder down,
/// and registers + starts the Windows service. Missing options are prompted for when run
/// interactively (e.g. double-clicked), and the process self-elevates via UAC if needed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SelfInstaller
{
    private const string ServiceName = "GitHubOrchestrator";
    private const string DisplayName = "GitHub Orchestrator";
    private const string ExeName = "orchestrator-service.exe";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static int Install(string[] opts)
    {
        if (!Environment.IsPrivilegedProcess)
            return RelaunchElevated("install", opts);

        try
        {
            var o = Parse(opts);
            var owner = o.GetValueOrDefault("repoowner") ?? PromptRequired("GitHub owner (user or org)");
            var repo = o.GetValueOrDefault("reponame") ?? PromptRequired("Control repo name");
            var token = o.GetValueOrDefault("token") ?? PromptSecret("Access token (leave blank for a public repo)");
            var branch = o.GetValueOrDefault("branch") ?? "main";
            var root = o.GetValueOrDefault("installroot") ?? @"C:\Orchestrator";
            var interval = int.TryParse(o.GetValueOrDefault("intervalminutes"), out var iv) ? iv : 60;

            Console.WriteLine($"\nInstalling to {root} (repo {owner}/{repo}@{branch}, every {interval} min)...");

            Directory.CreateDirectory(root);
            var target = Path.Combine(root, ExeName);

            if (ServiceExists()) { Console.WriteLine("Stopping existing service..."); Sc("stop", ServiceName); System.Threading.Thread.Sleep(2000); }

            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve own path.");
            if (!PathEquals(self, target))
                File.Copy(self, target, overwrite: true);

            WriteAppSettings(root, owner, repo, branch, token, interval);

            // Lock the folder to SYSTEM + Administrators.
            Run("icacls", root, "/inheritance:r", "/grant:r", "SYSTEM:(OI)(CI)F", "Administrators:(OI)(CI)F");

            var bin = $"\"{target}\"";
            if (ServiceExists())
                Sc("config", ServiceName, "binPath=", bin, "start=", "auto");
            else
                Sc("create", ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", DisplayName);
            Sc("description", ServiceName, "Syncs and manages programs from a GitHub manifest.");
            Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");
            Sc("start", ServiceName);

            Console.WriteLine($"\nDone. Service '{ServiceName}' is installed and running.");
            Console.WriteLine($"Logs: {Path.Combine(root, "logs")}");
            Pause();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nInstall failed: {ex.Message}");
            Pause();
            return 1;
        }
    }

    public static int Uninstall(string[] opts)
    {
        if (!Environment.IsPrivilegedProcess)
            return RelaunchElevated("uninstall", opts);

        try
        {
            var o = Parse(opts);
            var root = o.GetValueOrDefault("installroot") ?? @"C:\Orchestrator";
            var keepFiles = o.ContainsKey("keepfiles");
            var keepStartup = o.ContainsKey("keepstartup");

            if (ServiceExists())
            {
                Console.WriteLine("Stopping and deleting service...");
                Sc("stop", ServiceName);
                System.Threading.Thread.Sleep(2000);
                Sc("delete", ServiceName);
            }
            else Console.WriteLine("Service not installed.");

            if (!keepStartup) RemoveStartupEntries();

            if (!keepFiles && Directory.Exists(root))
            {
                Console.WriteLine($"Removing {root} ...");
                try { Directory.Delete(root, recursive: true); }
                catch (Exception ex) { Console.WriteLine($"(Could not fully remove {root}: {ex.Message} — the running exe may be in use.)"); }
            }

            Console.WriteLine("Uninstall complete.");
            Pause();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Uninstall failed: {ex.Message}");
            Pause();
            return 1;
        }
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            GitHub Orchestrator service

            Usage:
              orchestrator-service.exe install   [options]   Install and start the service
              orchestrator-service.exe uninstall [options]   Stop and remove the service
              orchestrator-service.exe run                   Run in the foreground (debugging)

            Install options (prompted if omitted):
              -RepoOwner <owner>        GitHub user/org that owns the control repo
              -RepoName  <repo>         Control repo name
              -Token     <pat>          Contents:Read PAT (omit for a public repo)
              -Branch    <name>         Control repo branch (default: main)
              -IntervalMinutes <n>      Sync interval (default: 60)
              -InstallRoot <path>       Install directory (default: C:\Orchestrator)

            Uninstall options:
              -InstallRoot <path>       Install directory (default: C:\Orchestrator)
              -KeepFiles                Leave files and logs on disk
              -KeepStartup              Leave Orch_* startup entries in place

            Double-clicking the exe runs 'install' interactively (prompts + UAC).
            """);
    }

    // ---- helpers ----

    private static void WriteAppSettings(string root, string owner, string repo, string branch, string token, int interval)
    {
        var cfg = new Dictionary<string, object>
        {
            ["Orchestrator"] = new Dictionary<string, object>
            {
                ["RootPath"] = root,
                ["RepoOwner"] = owner,
                ["RepoName"] = repo,
                ["Branch"] = branch,
                ["ManifestPath"] = "manifest.json",
                ["GitHubToken"] = token,
                ["SyncIntervalMinutes"] = interval,
                ["StartupRegistryKey"] = RunKey,
                ["RegistryEntryPrefix"] = "Orch_"
            }
        };
        File.WriteAllText(Path.Combine(root, "appsettings.json"),
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RemoveStartupEntries()
    {
        // HKLM Run values.
        using (var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true))
        {
            if (key is not null)
                foreach (var name in key.GetValueNames().Where(n => n.StartsWith("Orch_", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Removing startup entry {name}");
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
        }

        // Orch_* scheduled tasks.
        var (_, csv) = Capture("schtasks", "/Query", "/FO", "CSV", "/NH");
        foreach (var line in csv.Split('\n'))
        {
            var first = line.Split(',').FirstOrDefault()?.Trim().Trim('"').TrimStart('\\');
            if (!string.IsNullOrEmpty(first) && first.StartsWith("Orch_", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Removing scheduled task {first}");
                Run("schtasks", "/Delete", "/TN", first, "/F");
            }
        }
    }

    private static int RelaunchElevated(string verb, string[] opts)
    {
        if (!Environment.UserInteractive)
        {
            Console.Error.WriteLine("Run this from an elevated (Administrator) prompt.");
            return 1;
        }
        try
        {
            var args = new StringBuilder(verb);
            foreach (var a in opts) args.Append(' ').Append(Quote(a));
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = args.ToString(),
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not elevate (UAC declined?): {ex.Message}");
            return 1;
        }
    }

    /// <summary>Parse -Key Value / -Flag args into a lower-cased map.</summary>
    private static Dictionary<string, string> Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-')) continue;
            var key = args[i].TrimStart('-').ToLowerInvariant();
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) { map[key] = args[++i]; }
            else map[key] = "true"; // flag
        }
        return map;
    }

    private static string PromptRequired(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var v = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(v)) return v;
            Console.WriteLine("  (required)");
        }
    }

    private static string PromptSecret(string label)
    {
        Console.Write($"{label}: ");
        var sb = new StringBuilder();
        ConsoleKeyInfo k;
        while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static bool ServiceExists()
    {
        var (code, _) = Capture("sc.exe", "query", ServiceName);
        return code == 0;
    }

    private static void Sc(params string[] args) => Run("sc.exe", args);

    private static void Run(string file, params string[] args)
    {
        var (code, output) = Capture(file, args);
        if (code != 0)
            Console.WriteLine($"  [{file} {string.Join(' ', args)}] exit {code}: {output.Trim()}");
    }

    private static (int Code, string Output) Capture(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp);
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    private static void Pause()
    {
        if (!Environment.UserInteractive) return;
        Console.WriteLine("\nPress any key to close...");
        try { Console.ReadKey(intercept: true); } catch { /* no console */ }
    }
}
