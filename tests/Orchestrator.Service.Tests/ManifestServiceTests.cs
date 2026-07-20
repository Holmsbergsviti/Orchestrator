using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Service.Models;
using Orchestrator.Service.Services;
using Xunit;

namespace Orchestrator.Service.Tests;

public sealed class ManifestServiceTests
{
    private static ManifestService NewService()
        => new(new FakeConfigService(), NullLogger<ManifestService>.Instance);

    private static string Sha256Upper(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data));

    [Fact]
    public void NewProgram_IsPlannedAsInstall()
    {
        var svc = NewService();
        var remote = TestData.Manifest(TestData.Program("a"));

        var plan = svc.BuildPlan(remote, local: null, new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Install, action.Type);
        Assert.Equal("a", action.Program.Id);
    }

    [Fact]
    public void VersionBump_IsPlannedAsUpdate()
    {
        var svc = NewService();
        var local = TestData.Manifest(TestData.Program("a", version: "1.0"));
        var remote = TestData.Manifest(TestData.Program("a", version: "2.0"));

        var plan = svc.BuildPlan(remote, local, new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Update, action.Type);
        Assert.Equal("1.0", action.PreviousVersion);
    }

    [Fact]
    public void SameVersion_IntactFile_IsUpToDate()
    {
        var svc = NewService();
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var content = "payload"u8.ToArray();
            var hash = Sha256Upper(content);
            File.WriteAllBytes(Path.Combine(dir, "app.exe"), content);

            var prog = TestData.Program("a", installPath: dir, checksum: "sha256:" + hash);
            var plan = svc.BuildPlan(
                TestData.Manifest(prog),
                TestData.Manifest(TestData.Program("a", installPath: dir, checksum: "sha256:" + hash)),
                new Dictionary<string, string> { ["a"] = hash });

            var action = Assert.Single(plan.Actions);
            Assert.Equal(SyncActionType.UpToDate, action.Type);
            Assert.False(plan.HasWork);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SameVersion_MissingFile_IsRepairedAsInstall()
    {
        var svc = NewService();
        var dir = Path.Combine(Path.GetTempPath(), "orch-missing-" + Guid.NewGuid().ToString("N"));
        var prog = TestData.Program("a", installPath: dir, checksum: "sha256:ABCD");

        var plan = svc.BuildPlan(
            TestData.Manifest(prog),
            TestData.Manifest(TestData.Program("a", installPath: dir, checksum: "sha256:ABCD")),
            new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Install, action.Type);
    }

    [Fact]
    public void StatusDeleted_WhenInstalled_IsPlannedAsDelete()
    {
        var svc = NewService();
        var local = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Active));
        var remote = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Deleted));

        var plan = svc.BuildPlan(remote, local, new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Delete, action.Type);
    }

    [Fact]
    public void StatusDeleted_NeverInstalled_IsNoOp()
    {
        var svc = NewService();
        var remote = TestData.Manifest(
            TestData.Program("a", status: ProgramStatus.Deleted, installPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        var plan = svc.BuildPlan(remote, local: null, new Dictionary<string, string>());

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void RemovedFromManifestEntirely_IsPlannedAsDelete()
    {
        var svc = NewService();
        var local = TestData.Manifest(TestData.Program("gone", status: ProgramStatus.Active));
        var remote = TestData.Manifest(TestData.Program("other"));

        var plan = svc.BuildPlan(remote, local, new Dictionary<string, string>());

        Assert.Contains(plan.Actions, a => a.Type == SyncActionType.Delete && a.Program.Id == "gone");
        Assert.Contains(plan.Actions, a => a.Type == SyncActionType.Install && a.Program.Id == "other");
    }

    [Fact]
    public void StatusDeleted_IsNotDoubleCountedByRemovalSweep()
    {
        var svc = NewService();
        var local = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Active));
        var remote = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Deleted));

        var plan = svc.BuildPlan(remote, local, new Dictionary<string, string>());

        Assert.Single(plan.Actions, a => a.Type == SyncActionType.Delete && a.Program.Id == "a");
    }
}
