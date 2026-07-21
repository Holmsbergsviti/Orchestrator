// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The part that actually talks to GitHub. It downloads the manifest.json and the
//   individual program files. It uses GitHub's "Contents API" with the raw media
//   type, which is what lets it read files from PRIVATE repos using your token. A
//   program can point at a file either by a repo path or by a raw GitHub URL, and
//   this class figures out the right path either way.
// =====================================================================================

using System.Net;                     // for HttpStatusCode (e.g. 404 Not Found)
using System.Net.Http.Headers;        // for setting the Accept header
using System.Text.Json;               // for parsing the manifest JSON
using Microsoft.Extensions.Logging;   // for logging
using Orchestrator.Service.Models;    // for Manifest / ProgramEntry / config

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IGitHubClient   // the contract for GitHub access
{
    /// <summary>Fetch and deserialize the remote manifest. Returns null on network/parse failure.</summary>
    Task<Manifest?> GetManifestAsync(CancellationToken ct = default);

    /// <summary>Download the raw bytes for a program file.</summary>
    Task<byte[]> DownloadFileAsync(ProgramEntry program, CancellationToken ct = default);
}

/// <summary>
/// Talks to the GitHub Contents API with the "raw" media type so it works for
/// private repositories using a PAT. Files can be addressed by an explicit
/// repo-relative <c>path</c> or by parsing a raw.githubusercontent.com <c>url</c>.
/// </summary>
public sealed class GitHubClient : IGitHubClient
{
    public const string HttpClientName = "github";   // the name of the pre-configured HttpClient (set up in Program.cs)

    private static readonly JsonSerializerOptions JsonOpts = new()   // JSON parsing options
    {
        PropertyNameCaseInsensitive = true   // don't care about upper/lower case in field names
    };

    private readonly IHttpClientFactory _httpFactory;   // creates the shared, pre-configured HttpClient
    private readonly OrchestratorConfig _config;        // our settings (owner/repo/branch)
    private readonly ILogger<GitHubClient> _log;        // logger

    public GitHubClient(
        IHttpClientFactory httpFactory,
        IConfigService configService,
        ILogger<GitHubClient> log)   // dependencies from DI
    {
        _httpFactory = httpFactory;         // store the client factory
        _config = configService.Config;     // grab the settings
        _log = log;                         // store the logger
    }

    public async Task<Manifest?> GetManifestAsync(CancellationToken ct = default)
    {
        try
        {
            var bytes = await GetContentsRawAsync(_config.ManifestPath, ct);   // download the manifest file bytes
            return JsonSerializer.Deserialize<Manifest>(bytes, JsonOpts);      // parse them into a Manifest object
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch manifest from {Owner}/{Repo}@{Branch}",
                _config.RepoOwner, _config.RepoName, _config.Branch);   // log the failure...
            return null;                                                // ...and return null so the caller can skip this cycle
        }
    }

    public Task<byte[]> DownloadFileAsync(ProgramEntry program, CancellationToken ct = default)
    {
        var path = ResolvePath(program);   // work out the repo path for this program's file
        if (string.IsNullOrWhiteSpace(path))   // couldn't determine a path?
            throw new InvalidOperationException(
                $"Program '{program.Name}' has neither a resolvable 'path' nor 'url'.");
        return GetContentsRawAsync(path, ct);   // download the file bytes from that path
    }

    /// <summary>Resolve the repo-relative path from an entry's path or raw URL, logging on failure.</summary>
    private string? ResolvePath(ProgramEntry program)
    {
        var path = ResolveRepoPath(program);   // do the actual resolution (pure logic below)
        if (path is null && string.IsNullOrWhiteSpace(program.Path))   // nothing worked and there was no explicit path...
            _log.LogWarning("Could not parse repo path from url '{Url}' for {Name}", program.Url, program.Name);  // warn
        return path;   // return whatever we got (may be null)
    }

    /// <summary>
    /// Pure resolution of a program's repo-relative path: prefer an explicit <c>path</c>,
    /// otherwise parse a <c>raw.githubusercontent.com/{owner}/{repo}/{branch}/{path...}</c>
    /// URL, dropping the owner/repo/branch segments. Returns null if neither yields a path.
    /// </summary>
    public static string? ResolveRepoPath(ProgramEntry program)
    {
        if (!string.IsNullOrWhiteSpace(program.Path))   // an explicit path is the preferred source...
            return program.Path.TrimStart('/');         // ...just remove any leading slash

        if (string.IsNullOrWhiteSpace(program.Url))     // no path and no url?
            return null;                                // -> nothing we can do

        if (Uri.TryCreate(program.Url, UriKind.Absolute, out var uri) &&   // parse the url, and...
            uri.Host.Contains("githubusercontent", StringComparison.OrdinalIgnoreCase))  // ...make sure it's a GitHub raw url
        {
            var segs = uri.AbsolutePath.TrimStart('/').Split('/');   // split the path into segments
            if (segs.Length > 3)                                     // it should be owner/repo/branch/actual/path...
                return string.Join('/', segs.Skip(3)); // drop owner/repo/branch, keep the rest
        }

        return null;   // not a recognizable GitHub raw url -> give up
    }

    private async Task<byte[]> GetContentsRawAsync(string repoPath, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);   // grab the pre-configured GitHub HttpClient
        var requestUri =                                          // build the Contents API URL for this file
            $"/repos/{_config.RepoOwner}/{_config.RepoName}/contents/{Uri.EscapeDataString(repoPath).Replace("%2F", "/")}?ref={Uri.EscapeDataString(_config.Branch)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);   // create a GET request
        // "raw" media type returns file bytes directly instead of the JSON metadata wrapper.
        req.Headers.Accept.Clear();                                          // clear the default Accept header
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw"));  // ask for raw file bytes

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);  // send it; start reading as soon as headers arrive
        if (resp.StatusCode == HttpStatusCode.NotFound)   // 404 -> the path doesn't exist in the repo
            throw new FileNotFoundException($"GitHub path not found: {repoPath}");
        resp.EnsureSuccessStatusCode();   // any other non-success status -> throw

        return await resp.Content.ReadAsByteArrayAsync(ct);   // read and return the file's bytes
    }
}
