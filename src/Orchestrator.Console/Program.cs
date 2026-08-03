// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The entry point for the operator console. It reads settings, checks that you pointed
//   it at a real git clone of your control repo, starts a small local web server that
//   serves the page and its JSON endpoints, and opens your browser to it. This is the
//   program you run on your own PC to control the fleet.
//
//   Auth: the console has NO login by default (fine for the normal case — it only listens
//   on localhost, so only you can reach it). The moment 'Urls' binds to anything else — you
//   want to reach it from another machine, or over the internet for remote control — a
//   'Console:AccessToken' AND an HTTPS certificate become mandatory and the console refuses
//   to start without them. See docs/SETUP.md.
// =====================================================================================

using System.Diagnostics;                          // to open the browser
using System.Runtime.InteropServices;              // to pick the right "open" command per OS
using System.Security.Claims;                      // ClaimsIdentity/ClaimsPrincipal for the auth cookie
using System.Security.Cryptography.X509Certificates; // loading the HTTPS certificate
using Microsoft.AspNetCore.Authentication;          // AuthenticationProperties
using Microsoft.AspNetCore.Authentication.Cookies;  // cookie auth scheme
using Orchestrator.Console;                         // ConsoleOptions / ControlRepo / ConsoleAuth

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
builder.Services.AddSingleton<RelayHub>();
builder.Services.AddSingleton<ControlRepo>();
builder.Services.AddSingleton<LoginLockout>();

var listenUrl = builder.Configuration["Urls"] ?? "http://localhost:5080";
builder.WebHost.UseUrls(listenUrl);   // apply the configured address explicitly
var isLocalOnly = ConsoleAuth.IsLocalUrl(listenUrl);

// Load the HTTPS certificate (if configured) BEFORE the host is built — Kestrel options
// can only be set at this point. A configured-but-unreadable cert is a hard failure: better
// to refuse to start than silently fall back to plaintext.
X509Certificate2? cert = null;
if (!string.IsNullOrWhiteSpace(opt.CertPfxPath))
{
    try
    {
        cert = string.IsNullOrEmpty(opt.CertPfxPassword)
            ? new X509Certificate2(opt.CertPfxPath)
            : new X509Certificate2(opt.CertPfxPath, opt.CertPfxPassword);
    }
    catch (Exception ex)
    {
        System.Console.Error.WriteLine($"ERROR: Could not load 'Console:CertPfxPath' ({opt.CertPfxPath}): {ex.Message}");
        return 1;
    }
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = cert));

    var urlParts = listenUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (!urlParts.All(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
    {
        System.Console.Error.WriteLine(
            $"ERROR: 'Console:CertPfxPath' is set, but 'Urls' ({listenUrl}) doesn't use https://. " +
            "Change Urls to an https:// address to actually use the certificate.");
        return 1;
    }
}

// Cookie-based auth for the AccessToken. Signing in (POST /login) issues this cookie;
// everything else is gated on it by the middleware below, once a token is configured.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "orch_console_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // Secure flag when served over HTTPS
        o.LoginPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Fail fast, with a clear message, rather than quietly exposing an unprotected console.
if (!isLocalOnly)
{
    if (string.IsNullOrWhiteSpace(opt.AccessToken))
    {
        System.Console.Error.WriteLine(
            $"ERROR: 'Urls' ({listenUrl}) is bound beyond localhost, but 'Console:AccessToken' is not set.\n" +
            "       Set a long random Console:AccessToken before exposing the console — without it, anyone\n" +
            "       who can reach this address controls your whole fleet.");
        return 1;
    }
    if (cert is null)
    {
        System.Console.Error.WriteLine(
            $"ERROR: 'Urls' ({listenUrl}) is bound beyond localhost, but no 'Console:CertPfxPath' is configured.\n" +
            "       Plaintext HTTP would expose your AccessToken and every remote-control keystroke to\n" +
            "       anyone on the network path. Configure a certificate first — see docs/SETUP.md.");
        return 1;
    }
}

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

app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();

// Single choke point: every request needs a valid auth cookie, as soon as an AccessToken is
// configured at all (so it also protects a localhost console once you've set one up, not just
// a remotely-exposed one). Runs before static files, so index.html/remote.html are gated too,
// not just /api. Two routes are deliberately exempt from the COOKIE check: /login (that's how
// you get the cookie) and /relay/agent — the agent isn't a browser and never holds the
// operator's cookie; it authenticates with its own single-use session token instead (checked
// inside the endpoint itself, against RelayHub).
app.Use(async (ctx, next) =>
{
    var authRequired = !string.IsNullOrEmpty(opt.AccessToken);
    var isLoginRoute = ctx.Request.Path.StartsWithSegments("/login");
    var isAgentRelayRoute = ctx.Request.Path.StartsWithSegments("/relay/agent");
    if (!authRequired || isLoginRoute || isAgentRelayRoute || (ctx.User.Identity?.IsAuthenticated ?? false))
    {
        await next();
        return;
    }
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    ctx.Response.Redirect("/login");
});

app.MapGet("/login", (HttpContext ctx) =>
    Results.Content(LoginPage.Build(ctx.Request.Query.ContainsKey("error")), "text/html"));

app.MapPost("/login", async (HttpContext ctx, ConsoleOptions o, LoginLockout lockout) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (lockout.IsLocked(ip))
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    var form = await ctx.Request.ReadFormAsync();
    var token = form["token"].ToString();
    if (string.IsNullOrEmpty(o.AccessToken) || !ConsoleAuth.ConstantTimeEquals(token, o.AccessToken))
    {
        lockout.RecordFailure(ip);
        return Results.Redirect("/login?error=1");
    }
    lockout.Reset(ip);

    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "operator") }, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    return Results.Redirect("/");
});

app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

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

// Add a new program (optionally importing a local file), then commit + push.
app.MapPost("/api/add", async (AddRequest req, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var result = await r.AddProgramAsync(req, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// Trigger a one-time interactive "run now" of a program (rotates its runRequest token).
app.MapPost("/api/runnow", async (RunNowRequest req, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var result = await r.RunNowAsync(req.Id, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// Delete a program from the manifest entirely (uninstalls it from all machines).
app.MapPost("/api/delete", async (RunNowRequest req, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var result = await r.DeleteProgramAsync(req.Id, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// Send an admin command (shutdown/restart) to a machine.
app.MapPost("/api/command", async (CommandRequest req, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var result = await r.SendCommandAsync(req.MachineId, req.Action, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// Wake-on-LAN one or more machines (the waker agent sends the packets).
app.MapPost("/api/wake", async (WakeApiRequest req, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var result = await r.SendWakeAsync(req.MachineIds, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// Request a screenshot capture from a machine (captured on its next sync, in its logged-on user's session).
app.MapPost("/api/screenshot", async (ScreenshotApiRequest req, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var result = await r.SendScreenshotAsync(req.MachineId, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// Fetch a machine's latest captured screenshot image.
app.MapGet("/api/screenshot/{machineId}", async (string machineId, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var bytes = await r.GetLatestScreenshotAsync(machineId, ct);
        return bytes is null ? Results.NotFound() : Results.File(bytes, "image/jpeg");
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// Request a live remote-control session on a machine (captured on its next sync, in its
// logged-on user's session — same constraint as screenshots).
app.MapPost("/api/remote-session", async (ScreenshotApiRequest req, ControlRepo r, CancellationToken ct) =>
{
    try
    {
        var result = await r.StartRemoteSessionAsync(req.MachineId, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (GitException ex) { return Results.Problem(ex.Message, statusCode: 502, title: "git error"); }
});

// The operator's browser side of a remote-control session. Cookie-authenticated like every
// other route; RelayHub additionally checks the sessionId itself is one this console issued.
app.Map("/relay/view", async (HttpContext ctx, RelayHub relay) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    var sessionId = ctx.Request.Query["sessionId"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !relay.IsValid(sessionId))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await relay.RunViewerAsync(sessionId, ws, ctx.RequestAborted);
});

// The agent's side of a remote-control session. NOT cookie-gated (see the auth middleware
// above) — authenticates instead with the single-use session token issued when the console
// queued the request, presented as "Authorization: Bearer <token>".
app.Map("/relay/agent", async (HttpContext ctx, RelayHub relay) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    var machineId = ctx.Request.Query["machineId"].ToString();
    var authHeader = ctx.Request.Headers["Authorization"].ToString();
    var sessionId = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authHeader["Bearer ".Length..].Trim()
        : "";
    if (string.IsNullOrEmpty(sessionId) || !relay.IsValid(sessionId, machineId))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await relay.RunAgentAsync(sessionId, ws, ctx.RequestAborted);
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
