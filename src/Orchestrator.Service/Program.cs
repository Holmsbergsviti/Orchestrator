// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The entry point — the very first code that runs when orchestrator-service.exe
//   starts. It does two jobs. First, it checks if you ran it with a command word
//   like "install"/"uninstall" (or just double-clicked it) and, if so, hands off to
//   the self-installer. Otherwise it wires everything together (logging, the GitHub
//   HTTP client, all the services) and starts the long-running background worker
//   that actually does the syncing.
// =====================================================================================

using System.Net.Http.Headers;                        // for building the GitHub HTTP request headers
using Microsoft.Extensions.Hosting.WindowsServices;   // helpers to detect if we're running as a Windows service
using Microsoft.Extensions.Options;                   // for reading strongly-typed config (IOptions<>)
using Orchestrator.Service;                            // our own namespaces below
using Orchestrator.Service.Models;
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

// Bootstrap logger for early failures before the host is built.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();  // a basic console logger for very-early errors

try
{
    var builder = Host.CreateApplicationBuilder(args);  // start building the app host (DI container, config, etc.)

    // Run as a Windows Service when launched by the SCM; console when run interactively.
    builder.Services.AddWindowsService(o => o.ServiceName = OrchestratorDefaults.Instance.ServiceName);  // enables Windows-service behavior (name from defaults.json)

    builder.Services.Configure<OrchestratorConfig>(                                  // bind appsettings.json...
        builder.Configuration.GetSection(OrchestratorConfig.SectionName));           // ...the "Orchestrator" section -> OrchestratorConfig

    // Serilog: daily-rolling file under <RootPath>\logs plus console for interactive runs.
    builder.Services.AddSerilog((sp, cfg) =>
    {
        var conf = sp.GetRequiredService<IOptions<OrchestratorConfig>>().Value;  // read our config to find the log folder
        var logDir = conf.LogsPath;                                              // <root>\logs
        Directory.CreateDirectory(logDir);                                       // make sure that folder exists
        cfg.MinimumLevel.Information()                                           // log Information level and above
           .Enrich.FromLogContext()                                             // include contextual properties in each line
           .WriteTo.Console(                                                    // write logs to the console...
               outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] {Message:lj}{NewLine}{Exception}")  // ...with this format
           .WriteTo.File(                                                       // ...and to a file
               path: Path.Combine(logDir, "log-.txt"),                          // file name pattern (date gets inserted)
               rollingInterval: RollingInterval.Day,                            // start a new file each day
               retainedFileCountLimit: 90,                                      // keep 90 days of logs, delete older
               outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}");  // file line format
    });

    // Named HttpClient for the GitHub API (auth + UA set once).
    builder.Services.AddHttpClient(GitHubClient.HttpClientName, (sp, client) =>
    {
        var conf = sp.GetRequiredService<IOptions<OrchestratorConfig>>().Value;   // read config (for the token)
        client.BaseAddress = new Uri("https://api.github.com/");                  // all requests go to the GitHub API
        client.DefaultRequestHeaders.UserAgent.Add(                              // GitHub requires a User-Agent header
            new ProductInfoHeaderValue("GitHubOrchestrator", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(                                // ask for GitHub's JSON API format
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");  // pin the API version for stability
        if (!string.IsNullOrWhiteSpace(conf.GitHubToken))                        // if we have a token (private repo)...
            client.DefaultRequestHeaders.Authorization =                        // ...send it as a Bearer token
                new AuthenticationHeaderValue("Bearer", conf.GitHubToken);
        client.Timeout = TimeSpan.FromMinutes(5);                               // give big downloads up to 5 minutes
    });

    builder.Services.AddSingleton<IConfigService, ConfigService>();          // register each service so DI can create/inject it
    builder.Services.AddSingleton<IChecksumService, ChecksumService>();      // (one shared instance each -> "singleton")
    builder.Services.AddSingleton<IGitHubClient, GitHubClient>();
    builder.Services.AddSingleton<IRegistryService, RegistryService>();
    builder.Services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
    builder.Services.AddSingleton<IStartupManager, StartupManager>();
    builder.Services.AddSingleton<IManifestService, ManifestService>();
    builder.Services.AddSingleton<ISyncService, SyncService>();
    builder.Services.AddHostedService<Worker>();                             // register the background loop that drives everything

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
