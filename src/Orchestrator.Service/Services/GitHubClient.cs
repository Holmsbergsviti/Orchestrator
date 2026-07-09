using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

public interface IGitHubClient
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
    public const string HttpClientName = "github";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly OrchestratorConfig _config;
    private readonly ILogger<GitHubClient> _log;

    public GitHubClient(
        IHttpClientFactory httpFactory,
        IConfigService configService,
        ILogger<GitHubClient> log)
    {
        _httpFactory = httpFactory;
        _config = configService.Config;
        _log = log;
    }

    public async Task<Manifest?> GetManifestAsync(CancellationToken ct = default)
    {
        try
        {
            var bytes = await GetContentsRawAsync(_config.ManifestPath, ct);
            return JsonSerializer.Deserialize<Manifest>(bytes, JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch manifest from {Owner}/{Repo}@{Branch}",
                _config.RepoOwner, _config.RepoName, _config.Branch);
            return null;
        }
    }

    public Task<byte[]> DownloadFileAsync(ProgramEntry program, CancellationToken ct = default)
    {
        var path = ResolvePath(program);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"Program '{program.Name}' has neither a resolvable 'path' nor 'url'.");
        return GetContentsRawAsync(path, ct);
    }

    /// <summary>Resolve the repo-relative path from an entry's path or raw URL.</summary>
    private string? ResolvePath(ProgramEntry program)
    {
        if (!string.IsNullOrWhiteSpace(program.Path))
            return program.Path.TrimStart('/');

        if (string.IsNullOrWhiteSpace(program.Url))
            return null;

        // Expected: https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path...}
        if (Uri.TryCreate(program.Url, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("githubusercontent", StringComparison.OrdinalIgnoreCase))
        {
            var segs = uri.AbsolutePath.TrimStart('/').Split('/');
            if (segs.Length > 3)
                return string.Join('/', segs.Skip(3)); // drop owner/repo/branch
        }

        _log.LogWarning("Could not parse repo path from url '{Url}' for {Name}", program.Url, program.Name);
        return null;
    }

    private async Task<byte[]> GetContentsRawAsync(string repoPath, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        var requestUri =
            $"/repos/{_config.RepoOwner}/{_config.RepoName}/contents/{Uri.EscapeDataString(repoPath).Replace("%2F", "/")}?ref={Uri.EscapeDataString(_config.Branch)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
        // "raw" media type returns file bytes directly instead of the JSON metadata wrapper.
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw"));

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException($"GitHub path not found: {repoPath}");
        resp.EnsureSuccessStatusCode();

        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
