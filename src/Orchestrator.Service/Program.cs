// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The entry point — the very first code that runs when orchestrator-service.exe
//   starts. It does two jobs. First, it checks if you ran it with a command word
//   like "install"/"uninstall" (or just double-clicked it) and, if so, hands off to
//   the self-installer. Otherwise it wires everything together (logging, the GitHub
//   HTTP client, all the services) and starts the long-running background worker
//   that actually does the syncing.
// =====================================================================================

using Microsoft.Extensions.DependencyInjection;       // for resolving services (GetRequiredService)
using Microsoft.Extensions.Hosting.WindowsServices;   // helpers to detect if we're running as a Windows service
using Orchestrator.Service;                            // our own namespaces below
using Orchestrator.Service.Services;
using Serilog;                                         // the logging library

// CLI verbs (self-installer). Handled before the host so the single exe can set
// itself up as a service. On non-Windows these are skipped and the host runs.
if (OperatingSystem.IsWindows())                       // these commands only make sense on Windows
{
    var verb = args.Length > 0 ? args[0].ToLowerInvariant() : null;  // the first command-line word, if any (lower-cased)
    switch (verb)
    {
        case "install": return SelfInstaller.Install(args[1..]);      // "install" -> run the installer with the rest of the args
        case "uninstall": return SelfInstaller.Uninstall(args[1..]);  // "uninstall" -> run the uninstaller
        case "help" or "-h" or "--help" or "/?": SelfInstaller.PrintUsage(); return 0;  // any help flag -> print usage and exit
        // Double-clicked (interactive, not launched by the SCM): run the installer.
        case null when Environment.UserInteractive && !WindowsServiceHelpers.IsWindowsService():
            return SelfInstaller.Install(args);                       // no args + double-clicked -> treat as "install"
        // "run" or an SCM launch falls through to the host below.
    }
}

// Gated launcher: startup entries call "run-program <id>". This checks the current
// manifest and only launches the program if it's still active + targeted here.
if ((args.Length > 0 ? args[0].ToLowerInvariant() : null) == "run-program")
    return RunLauncher(args);

// Bootstrap logger for early failures before the host is built.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();  // a basic console logger for very-early errors

try
{
    var builder = Host.CreateApplicationBuilder(args);  // start building the app host (DI container, config, etc.)

    // Run as a Windows Service when launched by the SCM; console when run interactively.
    builder.Services.AddWindowsService(o => o.ServiceName = OrchestratorDefaults.Instance.ServiceName);  // enables Windows-service behavior (name from defaults.json)

    ServiceRegistration.AddOrchestratorSerilog(builder);   // file + console logging
    ServiceRegistration.AddOrchestratorServices(builder);  // config, GitHub client, all services (shared with the launcher)
    builder.Services.AddHostedService<Worker>();           // the background loop that drives everything

    var host = builder.Build();   // finalize the container and build the host
    host.Run();                   // start running and block here until the service is stopped
    return 0;                     // clean exit
}
catch (Exception ex)
{
    Log.Fatal(ex, "Orchestrator terminated unexpectedly");  // log any startup/runtime crash...
    return 1;                                               // ...and exit with an error code
}
finally
{
    Log.CloseAndFlush();  // make sure all buffered log lines are written out before we quit
}

// Runs the gated launcher for "run-program <id>": build a minimal host (same services as
// the main path, minus the background Worker), resolve the launcher, and run it once.
static int RunLauncher(string[] args)
{
    var id = args.Length > 1 ? args[1] : null;
    if (string.IsNullOrWhiteSpace(id))
    {
        Console.Error.WriteLine("usage: run-program <programId>");
        return 2;
    }
    try
    {
        // ContentRoot = the exe's folder so appsettings.json (next to the exe) is found even
        // though the launcher isn't started as a Windows service.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        ServiceRegistration.AddOrchestratorSerilog(builder);
        ServiceRegistration.AddOrchestratorServices(builder);

        using var host = builder.Build();
        var launcher = host.Services.GetRequiredService<IProgramLauncher>();
        return launcher.LaunchIfActiveAsync(id, CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"run-program failed: {ex.Message}");
        return 1;
    }
    finally
    {
        Log.CloseAndFlush();
    }
}
