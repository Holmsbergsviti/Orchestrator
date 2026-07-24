// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The entry point for the operator console. It reads settings, checks that you pointed
//   it at a real git clone of your control repo, starts a small local web server that
//   serves the page and three JSON endpoints (load state, save changes), and opens your
//   browser to it. This is the program you run on your own PC to control the fleet.
// =====================================================================================

using System.Diagnostics;                     // to open the browser
using System.Runtime.InteropServices;         // to pick the right "open" command per OS
using Orchestrator.Console;                    // ConsoleOptions / ControlRepo

// A first bare argument (not a --switch) is a convenience for the repo path. Pull it out
// BEFORE handing args to the config system — on macOS/Linux a path starts with '/', which
// the command-line config provider would otherwise try to parse as a switch.
string? repoArg = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
var configArgs = repoArg is null ? args : args[1..];

var builder = WebApplication.CreateBuilder(configArgs);

// Bind the "Console" settings section (also honors --Console:ControlRepoPath=... etc).
var opt = new ConsoleOptions();
builder.Configuration.GetSection(ConsoleOptions.SectionName).Bind(opt);
if (repoArg is not null) opt.ControlRepoPath = repoArg;   // bare arg wins
builder.Services.AddSingleton(opt);
builder.Services.AddSingleton<ControlRepo>();

var listenUrl = builder.Configuration["Urls"] ?? "http://localhost:5080";
builder.WebHost.UseUrls(listenUrl);   // apply the configured address explicitly

var app = builder.Build();

// Validate the repo up front with a clear message rather than failing per-request.
var repo = app.Services.GetRequiredService<ControlRepo>();
if (string.IsNullOrWhiteSpace(opt.ControlRepoPath) || !repo.RepoIsValid())
{
    System.Console.Error.WriteLine(
        "ERROR: Set 'Console:ControlRepoPath' in appsettings.json (or pass the repo path as the first argument)\n" +
        "       to a local git clone of your control repo. Current value: " +
        (string.IsNullOrWhiteSpace(opt.ControlRepoPath) ? "<empty>" : opt.ControlRepoPath));
    return 1;
}

app.UseDefaultFiles();   // serve wwwroot/index.html at "/"
app.UseStaticFiles();

// Load the combined fleet + programs view.
app.MapGet("/api/state", async (ControlRepo r, CancellationToken ct) =>
{
    try { return Results.Ok(await r.LoadStateAsync(ct)); }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// Save targeting + label edits (commits and pushes).
app.MapPost("/api/save", async (SaveRequest req, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var result = await r.SaveAsync(req, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// A tiny health/info endpoint the page uses to show which repo it's driving.
app.MapGet("/api/info", (ControlRepo r) => Results.Ok(new { repoPath = r.RepoPath }));

System.Console.WriteLine($"Orchestrator console driving repo: {opt.ControlRepoPath}");
System.Console.WriteLine($"Open {listenUrl} in your browser.");
if (opt.OpenBrowser) TryOpenBrowser(listenUrl);

app.Run();
return 0;

// Best-effort: launch the default browser on the local URL.
static void TryOpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
    catch { /* not fatal — the URL is printed above */ }
}
