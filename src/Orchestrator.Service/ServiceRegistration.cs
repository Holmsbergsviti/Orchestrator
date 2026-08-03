// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Shared wiring so both entry points — the long-running service AND the short-lived
//   "run-program <id>" launcher — register the exact same services (config, GitHub
//   client, manifest brain, etc.) and the same file logging. Keeping it in one place
//   means the launcher can never drift out of sync with how the service is configured.
// =====================================================================================

using System.Net.Http.Headers;                        // GitHub HTTP headers
using Microsoft.Extensions.DependencyInjection;       // service registration
using Microsoft.Extensions.Hosting;                   // HostApplicationBuilder
using Microsoft.Extensions.Options;                   // IOptions<>
using Orchestrator.Service.Models;                    // OrchestratorConfig
using Orchestrator.Service.Services;                  // the service types
using Serilog;                                        // logging

namespace Orchestrator.Service;

/// <summary>Common host configuration shared by the service host and the launcher.</summary>
public static class ServiceRegistration
{
    /// <summary>Bind config, the named GitHub HttpClient, and every orchestrator service.</summary>
    public static void AddOrchestratorServices(HostApplicationBuilder builder)
    {
        builder.Services.Configure<OrchestratorConfig>(
            builder.Configuration.GetSection(OrchestratorConfig.SectionName));   // appsettings "Orchestrator" section

        // Named HttpClient for the GitHub API (auth + UA set once).
        builder.Services.AddHttpClient(GitHubClient.HttpClientName, (sp, client) =>
        {
            var conf = sp.GetRequiredService<IOptions<OrchestratorConfig>>().Value;
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitHubOrchestrator", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(conf.GitHubToken))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", conf.GitHubToken);
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        builder.Services.AddSingleton<IConfigService, ConfigService>();
        builder.Services.AddSingleton<IChecksumService, ChecksumService>();
        builder.Services.AddSingleton<IGitHubClient, GitHubClient>();
        builder.Services.AddSingleton<IRegistryService, RegistryService>();
        builder.Services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
        builder.Services.AddSingleton<IStartupManager, StartupManager>();
        builder.Services.AddSingleton<IManifestService, ManifestService>();
        builder.Services.AddSingleton<IFleetReporter, FleetReporter>();
        builder.Services.AddSingleton<ISyncService, SyncService>();
        builder.Services.AddSingleton<IProgramLauncher, ProgramLauncher>();
        builder.Services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
        builder.Services.AddSingleton<IScreenshotService, ScreenshotService>();
        builder.Services.AddSingleton<IRemoteSessionService, RemoteSessionService>();
    }

    /// <summary>Serilog: daily-rolling file under &lt;RootPath&gt;\logs plus console.</summary>
    public static void AddOrchestratorSerilog(HostApplicationBuilder builder)
    {
        builder.Services.AddSerilog((sp, cfg) =>
        {
            var conf = sp.GetRequiredService<IOptions<OrchestratorConfig>>().Value;
            var logDir = conf.LogsPath;
            Directory.CreateDirectory(logDir);
            cfg.MinimumLevel.Information()
               .Enrich.FromLogContext()
               .WriteTo.Console(
                   outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] {Message:lj}{NewLine}{Exception}")
               .WriteTo.File(
                   path: Path.Combine(logDir, "log-.txt"),
                   rollingInterval: RollingInterval.Day,
                   retainedFileCountLimit: 90,
                   outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        });
    }
}
