// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks the "separate state repo" split that lets the control-repo token be read-only:
//   writes (heartbeats, screenshots) go to the state repo through the state client, reads
//   stay on the control repo, and the write-ability probe reads GitHub's answers right.
//   GitHub is faked by a handler that records each request and replies with a set status.
// =====================================================================================

using System.Net;                                  // HttpStatusCode
using Microsoft.Extensions.Logging.Abstractions;   // NullLogger
using Orchestrator.Service.Models;                 // OrchestratorConfig / MachineConfig
using Orchestrator.Service.Services;               // GitHubClient
using Xunit;                                       // the test framework

namespace Orchestrator.Service.Tests;

public sealed class StateRepoTests
{
    /// <summary>Records every request (with the named client it went through) and answers with
    /// whatever <see cref="Reply"/> says.</summary>
    private sealed class FakeGitHub : IHttpClientFactory
    {
        public readonly List<(string Client, HttpMethod Method, string Path)> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public HttpClient CreateClient(string name)
            => new(new Handler(this, name)) { BaseAddress = new Uri("https://api.github.com/") };

        private sealed class Handler(FakeGitHub owner, string name) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Requests.Add((name, request.Method, request.RequestUri!.AbsolutePath));
                return Task.FromResult(owner.Reply(request));
            }
        }
    }

    private sealed class FakeConfig(OrchestratorConfig config) : IConfigService
    {
        public OrchestratorConfig Config { get; } = config;
        public MachineConfig LoadOrCreateMachineConfig() => new();
        public void SaveMachineConfig(MachineConfig config) { }
        public void EnsureDirectories() { }
    }

    private static GitHubClient Client(FakeGitHub fake, bool separate) => new(fake, new FakeConfig(new OrchestratorConfig
    {
        RepoOwner = "me", RepoName = "control", GitHubToken = "read",
        StateRepoOwner = separate ? "me" : "", StateRepoName = separate ? "control-state" : "", StateToken = separate ? "write" : ""
    }), NullLogger<GitHubClient>.Instance);

    [Fact]
    public async Task Writes_GoToTheStateRepo_ThroughTheStateClient()
    {
        var fake = new FakeGitHub();
        await Client(fake, separate: true).PutFileAsync("state/m1.json", new byte[] { 1 }, "fleet-state", "hb", sha: null);

        var put = Assert.Single(fake.Requests);
        Assert.Equal(GitHubClient.StateHttpClientName, put.Client);   // the state token, not the control-repo token
        Assert.Equal("/repos/me/control-state/contents/state/m1.json", put.Path);
    }

    [Fact]
    public async Task Writes_StayOnTheControlRepo_WhenNoStateRepoIsSet()
    {
        var fake = new FakeGitHub();
        await Client(fake, separate: false).PutFileAsync("state/m1.json", new byte[] { 1 }, "fleet-state", "hb", sha: null);
        Assert.Equal("/repos/me/control/contents/state/m1.json", Assert.Single(fake.Requests).Path);   // unchanged for existing installs
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]   // past the permission check: this token could push
    [InlineData(HttpStatusCode.Forbidden, false)]            // a read-only fine-grained token
    [InlineData(HttpStatusCode.NotFound, false)]             // GitHub's answer when you can't write a private repo
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]    // outage: don't guess
    public async Task WriteProbe_ReadsGitHubsAnswer(HttpStatusCode status, bool? expected)
    {
        var fake = new FakeGitHub { Reply = _ => new HttpResponseMessage(status) };
        Assert.Equal(expected, await Client(fake, separate: true).CanControlTokenWriteAsync());

        var probe = Assert.Single(fake.Requests);
        Assert.Equal(GitHubClient.HttpClientName, probe.Client);       // it's the CONTROL token being tested
        Assert.Equal("/repos/me/control/git/refs", probe.Path);
    }

    [Theory]
    [InlineData("scripts/install.ps1", "StateRepoOwner")]
    [InlineData("scripts/update-agent.ps1", "install.ps1")]
    public void InstallerScripts_AreEmbeddedInTheAgent(string resource, string mustContain)
    {
        // Self-update only downloads the exe; these embedded copies are how a machine's on-disk
        // install.ps1 learns about new settings (InstalledScripts). If one went missing, every
        // update would silently drop the state-repo settings again.
        using var stream = typeof(InstalledScripts).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        Assert.Contains(mustContain, new StreamReader(stream!).ReadToEnd());
    }
}
