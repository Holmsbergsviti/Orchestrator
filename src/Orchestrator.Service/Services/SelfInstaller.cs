// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Lets the single exe install itself — no separate scripts needed. When you run
//   "orchestrator-service.exe install" (or just double-click it), this code copies
//   the exe into C:\Windows\Orch, writes appsettings.json, locks the folder down, and
//   registers + starts the Windows service. If you didn't pass the needed options it
//   asks for them, and if you're not running as admin it re-launches itself elevated
//   through a UAC prompt. "uninstall" does the reverse.
// =====================================================================================

using System.Diagnostics;          // for launching child processes (sc.exe, icacls, itself)
using System.Runtime.Versioning;   // for the [SupportedOSPlatform] Windows-only marker
using System.Text;                 // for StringBuilder (building arg strings / reading secrets)
using System.Text.Json;            // for writing appsettings.json
using Microsoft.Win32;             // for cleaning up registry startup entries

namespace Orchestrator.Service.Services;   // groups this with the other services

/// <summary>
/// Turns the service exe into its own installer. <c>orchestrator-service.exe install</c>
/// copies itself to the install root, writes appsettings.json, locks the folder down,
/// and registers + starts the Windows service. Missing options are prompted for when run
/// interactively (e.g. double-clicked), and the process self-elevates via UAC if needed.
/// </summary>
[SupportedOSPlatform("windows")]   // Windows-only
public static class SelfInstaller
{
    private static readonly OrchestratorDefaults D = OrchestratorDefaults.Instance;   // the shared names/paths from defaults.json
    private static readonly string ServiceName = D.ServiceName;         // the service's internal name (from defaults.json)
    private static readonly string DisplayName = D.ServiceDisplayName;  // the friendly name shown in Services (from defaults.json)
    private static readonly string ExeName = D.ExeName;                 // the executable file name (from defaults.json)
    private static readonly string RunKey = D.RegistryRunKey;           // registry key for startup entries (from defaults.json)

    public static int Install(string[] opts)
    {
        if (!Environment.IsPrivilegedProcess)          // not running as admin?
            return RelaunchElevated("install", opts);  // -> relaunch ourselves with a UAC prompt

        try
        {
            var o = Parse(opts);   // turn the -Key Value arguments into a lookup map
            var owner = o.GetValueOrDefault("repoowner") ?? PromptRequired("GitHub owner (user or org)");   // repo owner (ask if missing)
            var repo = o.GetValueOrDefault("reponame") ?? PromptRequired("Control repo name");              // repo name (ask if missing)
            var token = o.GetValueOrDefault("token") ?? PromptSecret("Access token (leave blank for a public repo)");  // token (ask, hidden)
            var branch = o.GetValueOrDefault("branch") ?? D.DefaultBranch;                                  // branch (from defaults.json)
            var root = o.GetValueOrDefault("installroot") ?? D.InstallRoot;                                 // install folder (from defaults.json)
            var interval = int.TryParse(o.GetValueOrDefault("intervalminutes"), out var iv) ? iv : D.DefaultSyncIntervalMinutes;  // interval (from defaults.json)

            Console.WriteLine($"\nInstalling to {root} (repo {owner}/{repo}@{branch}, every {interval} min)...");  // summary line

            Directory.CreateDirectory(root);           // make the install folder
            var target = Path.Combine(root, ExeName);  // where the exe should end up

            if (ServiceExists()) { Console.WriteLine("Stopping existing service..."); Sc("stop", ServiceName); System.Threading.Thread.Sleep(2000); }  // stop an old copy first

            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve own path.");  // path to the running exe
            if (!PathEquals(self, target))             // if we're not already running from the target...
                File.Copy(self, target, overwrite: true);  // ...copy ourselves there

            WriteAppSettings(root, owner, repo, branch, token, interval);   // write the settings file

            // Lock the folder to SYSTEM + Administrators.
            Run("icacls", root, "/inheritance:r", "/grant:r", "SYSTEM:(OI)(CI)F", "Administrators:(OI)(CI)F");  // restrict folder permissions

            var bin = $"\"{target}\"";                 // the quoted exe path for the service definition
            if (ServiceExists())
                Sc("config", ServiceName, "binPath=", bin, "start=", "auto");   // existing service -> repoint + auto-start
            else
                Sc("create", ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", DisplayName);  // new service -> create it
            Sc("description", ServiceName, D.ServiceDescription);   // set the description (from defaults.json)
            Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");  // auto-restart on crash
            Sc("start", ServiceName);                  // start the service now (first sync runs immediately)

            Console.WriteLine($"\nDone. Service '{ServiceName}' is installed and running.");   // success message
            Console.WriteLine($"Logs: {Path.Combine(root, "logs")}");                          // where to find logs
            Pause();                                   // wait for a keypress if interactive
            return 0;                                  // success exit code
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nInstall failed: {ex.Message}");   // report any failure
            Pause();
            return 1;                                                    // error exit code
        }
    }

    public static int Uninstall(string[] opts)
    {
        if (!Environment.IsPrivilegedProcess)             // not admin?
            return RelaunchElevated("uninstall", opts);   // -> relaunch elevated

        try
        {
            var o = Parse(opts);   // parse the arguments
            var root = o.GetValueOrDefault("installroot") ?? D.InstallRoot;   // install folder (from defaults.json)
            var keepFiles = o.ContainsKey("keepfiles");        // -KeepFiles flag present?
            var keepStartup = o.ContainsKey("keepstartup");    // -KeepStartup flag present?

            if (ServiceExists())   // is the service installed?
            {
                Console.WriteLine("Stopping and deleting service...");
                Sc("stop", ServiceName);                   // stop it
                System.Threading.Thread.Sleep(2000);       // let Windows release the exe
                Sc("delete", ServiceName);                 // delete the service registration
            }
            else Console.WriteLine("Service not installed.");   // nothing to remove

            if (!keepStartup) RemoveStartupEntries();      // unless asked to keep them, clean up Orch_* startup entries

            if (!keepFiles && Directory.Exists(root))      // unless keeping files, and if the folder exists...
            {
                Console.WriteLine($"Removing {root} ...");
                try { Directory.Delete(root, recursive: true); }   // delete the whole install folder
                catch (Exception ex) { Console.WriteLine($"(Could not fully remove {root}: {ex.Message} — the running exe may be in use.)"); }  // may fail if exe is in use
            }

            Console.WriteLine("Uninstall complete.");   // success message
            Pause();
            return 0;                                   // success exit code
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Uninstall failed: {ex.Message}");   // report failure
            Pause();
            return 1;                                                    // error exit code
        }
    }

    public static void PrintUsage()
    {
        Console.WriteLine(   // print the help text (verbatim raw string)
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
              -InstallRoot <path>       Install directory (default: C:\Windows\Orch)

            Uninstall options:
              -InstallRoot <path>       Install directory (default: C:\Windows\Orch)
              -KeepFiles                Leave files and logs on disk
              -KeepStartup              Leave Orch_* startup entries in place

            Double-clicking the exe runs 'install' interactively (prompts + UAC).
            """);
    }

    // ---- helpers ----

    private static void WriteAppSettings(string root, string owner, string repo, string branch, string token, int interval)
    {
        var cfg = new Dictionary<string, object>   // build the settings structure...
        {
            ["Orchestrator"] = new Dictionary<string, object>   // ...under the "Orchestrator" section
            {
                ["RootPath"] = root,                     // base folder
                ["RepoOwner"] = owner,                   // GitHub owner
                ["RepoName"] = repo,                     // GitHub repo
                ["Branch"] = branch,                     // branch to read
                ["ManifestPath"] = D.ManifestFileName,   // manifest file name in the repo (from defaults.json)
                ["GitHubToken"] = token,                 // access token (blank if public)
                ["SyncIntervalMinutes"] = interval,      // minutes between syncs
                ["StartupRegistryKey"] = RunKey,         // registry key for startup entries
                ["RegistryEntryPrefix"] = D.RegistryEntryPrefix   // prefix for our entry names (from defaults.json)
            }
        };
        File.WriteAllText(Path.Combine(root, "appsettings.json"),   // save it as appsettings.json...
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));  // ...pretty-printed
    }

    private static void RemoveStartupEntries()
    {
        // HKLM Run values.
        using (var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true))   // open the Run key
        {
            if (key is not null)
                foreach (var name in key.GetValueNames().Where(n => n.StartsWith(D.RegistryEntryPrefix, StringComparison.OrdinalIgnoreCase)))  // each Orch_* value
                {
                    Console.WriteLine($"Removing startup entry {name}");
                    key.DeleteValue(name, throwOnMissingValue: false);   // delete it
                }
        }

        // Orch_* scheduled tasks.
        var (_, csv) = Capture("schtasks", "/Query", "/FO", "CSV", "/NH");   // list all scheduled tasks as CSV (no header)
        foreach (var line in csv.Split('\n'))   // walk each line...
        {
            var first = line.Split(',').FirstOrDefault()?.Trim().Trim('"').TrimStart('\\');   // the task name is the first CSV column
            if (!string.IsNullOrEmpty(first) && first.StartsWith(D.RegistryEntryPrefix, StringComparison.OrdinalIgnoreCase))   // ours?
            {
                Console.WriteLine($"Removing scheduled task {first}");
                Run("schtasks", "/Delete", "/TN", first, "/F");   // delete it
            }
        }
    }

    private static int RelaunchElevated(string verb, string[] opts)
    {
        if (!Environment.UserInteractive)   // running headless (e.g. as a service) with no way to prompt for UAC?
        {
            Console.Error.WriteLine("Run this from an elevated (Administrator) prompt.");   // tell the user
            return 1;                                                                       // and fail
        }
        try
        {
            var args = new StringBuilder(verb);                       // start the argument string with the verb
            foreach (var a in opts) args.Append(' ').Append(Quote(a));  // append each original option (quoted if needed)
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,   // relaunch this same exe
                Arguments = args.ToString(),           // with the same verb + options
                UseShellExecute = true,                // required to trigger UAC
                Verb = "runas"                         // "runas" = request elevation (UAC prompt)
            };
            Process.Start(psi);   // launch the elevated copy
            return 0;             // this (non-elevated) copy exits successfully
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not elevate (UAC declined?): {ex.Message}");   // user likely clicked "No" on UAC
            return 1;
        }
    }

    /// <summary>Parse -Key Value / -Flag args into a lower-cased map.</summary>
    private static Dictionary<string, string> Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // the resulting key -> value lookup
        for (var i = 0; i < args.Length; i++)   // walk the arguments
        {
            if (!args[i].StartsWith('-')) continue;                  // skip anything not starting with '-'
            var key = args[i].TrimStart('-').ToLowerInvariant();     // the key name without the dash, lower-cased
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) { map[key] = args[++i]; }  // next token is its value
            else map[key] = "true"; // flag
        }
        return map;   // return the parsed options
    }

    private static string PromptRequired(string label)
    {
        while (true)   // keep asking until we get a non-empty answer
        {
            Console.Write($"{label}: ");                 // show the prompt
            var v = Console.ReadLine()?.Trim();          // read the typed line
            if (!string.IsNullOrEmpty(v)) return v;      // got something -> return it
            Console.WriteLine("  (required)");           // empty -> remind them it's required and loop
        }
    }

    private static string PromptSecret(string label)
    {
        Console.Write($"{label}: ");                     // show the prompt
        var sb = new StringBuilder();                    // collect the typed characters here
        ConsoleKeyInfo k;                                // the current key pressed
        while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)   // read keys silently until Enter
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }   // backspace -> remove last char
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);               // normal char -> add it
        }
        Console.WriteLine();       // move to a new line after Enter
        return sb.ToString();      // return the entered secret
    }

    private static bool ServiceExists()
    {
        var (code, _) = Capture("sc.exe", "query", ServiceName);   // ask sc.exe about the service
        return code == 0;                                          // exit code 0 means it exists
    }

    private static void Sc(params string[] args) => Run("sc.exe", args);   // shorthand for running sc.exe

    private static void Run(string file, params string[] args)
    {
        var (code, output) = Capture(file, args);   // run the process and capture its result
        if (code != 0)                              // if it failed...
            Console.WriteLine($"  [{file} {string.Join(' ', args)}] exit {code}: {output.Trim()}");  // print the details
    }

    private static (int Code, string Output) Capture(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)   // configure how to start the process
        {
            RedirectStandardOutput = true,   // capture normal output
            RedirectStandardError = true,    // capture error output
            UseShellExecute = false,         // run directly so we can capture output
            CreateNoWindow = true            // no popup window
        };
        foreach (var a in args) psi.ArgumentList.Add(a);   // add each argument safely
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");  // start it
        var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();   // read all output (normal + error)
        p.WaitForExit();                                                         // wait for it to finish
        return (p.ExitCode, outp);                                               // return exit code + combined output
    }

    private static bool PathEquals(string a, string b) =>   // compare two paths for equality...
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);  // ...normalized + case-insensitive

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;   // wrap in quotes only if it contains a space

    private static void Pause()
    {
        if (!Environment.UserInteractive) return;        // no console? -> don't wait
        Console.WriteLine("\nPress any key to close...");  // otherwise prompt...
        try { Console.ReadKey(intercept: true); } catch { /* no console */ }   // ...and wait for a keypress
    }
}
