using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Orchestrator.Service;
using Orchestrator.Service.Models;
using Orchestrator.Service.Services;
using Serilog;

// Bootstrap logger for early failures before the host is built.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Run as a Windows Service when launched by the SCM; console when run interactively.
    builder.Services.AddWindowsService(o => o.ServiceName = "GitHubOrchestrator");

    builder.Services.Configure<OrchestratorConfig>(
        builder.Configuration.GetSection(OrchestratorConfig.SectionName));

    // Serilog: daily-rolling file under <RootPath>\logs plus console for interactive runs.
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

    // Named HttpClient for the GitHub API (auth + UA set once).
    builder.Services.AddHttpClient(GitHubClient.HttpClientName, (sp, client) =>
    {
        var conf = sp.GetRequiredService<IOptions<OrchestratorConfig>>().Value;
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("GitHubOrchestrator", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(conf.GitHubToken))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", conf.GitHubToken);
        client.Timeout = TimeSpan.FromMinutes(5);
    });

    builder.Services.AddSingleton<IConfigService, ConfigService>();
    builder.Services.AddSingleton<IChecksumService, ChecksumService>();
    builder.Services.AddSingleton<IGitHubClient, GitHubClient>();
    builder.Services.AddSingleton<IRegistryService, RegistryService>();
    builder.Services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
    builder.Services.AddSingleton<IStartupManager, StartupManager>();
    builder.Services.AddSingleton<IManifestService, ManifestService>();
    builder.Services.AddSingleton<ISyncService, SyncService>();
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Orchestrator terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
