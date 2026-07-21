// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the planning "brain" (BuildPlan). They cover the key
//   scenarios: a new program becomes an Install, a version change becomes an Update,
//   an intact same-version file is UpToDate, a missing file is repaired via Install,
//   a program marked deleted (or removed from the manifest entirely) becomes a Delete,
//   and that a deleted program isn't counted twice.
// =====================================================================================

using System.Security.Cryptography;                    // to compute hashes for the "intact file" test
using Microsoft.Extensions.Logging.Abstractions;       // a no-op logger for the service under test
using Orchestrator.Service.Models;                     // for the model classes
using Orchestrator.Service.Services;                   // the code being tested
using Xunit;                                           // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class ManifestServiceTests
{
    private static ManifestService NewService()   // helper: build a ManifestService with fake config + null logger
        => new(new FakeConfigService(), NullLogger<ManifestService>.Instance);

    private static string Sha256Upper(byte[] data)   // helper: hash bytes to upper-case hex
        => Convert.ToHexString(SHA256.HashData(data));

    [Fact]
    public void NewProgram_IsPlannedAsInstall()
    {
        var svc = NewService();                                // the service under test
        var remote = TestData.Manifest(TestData.Program("a")); // remote has one program, nothing installed locally

        var plan = svc.BuildPlan(remote, local: null, new Dictionary<string, string>());  // build the plan

        var action = Assert.Single(plan.Actions);              // exactly one action expected
        Assert.Equal(SyncActionType.Install, action.Type);     // and it should be an Install
        Assert.Equal("a", action.Program.Id);                  // for program "a"
    }

    [Fact]
    public void VersionBump_IsPlannedAsUpdate()
    {
        var svc = NewService();
        var local = TestData.Manifest(TestData.Program("a", version: "1.0"));   // we had v1.0
        var remote = TestData.Manifest(TestData.Program("a", version: "2.0"));  // remote now says v2.0

        var plan = svc.BuildPlan(remote, local, new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);              // one action
        Assert.Equal(SyncActionType.Update, action.Type);      // an Update
        Assert.Equal("1.0", action.PreviousVersion);           // remembering the old version
    }

    [Fact]
    public void SameVersion_IntactFile_IsUpToDate()
    {
        var svc = NewService();
        var dir = Directory.CreateTempSubdirectory().FullName;  // a real temp folder to hold the "installed" file
        try
        {
            var content = "payload"u8.ToArray();                       // some file bytes
            var hash = Sha256Upper(content);                          // their fingerprint
            File.WriteAllBytes(Path.Combine(dir, "app.exe"), content); // write the file so it exists on disk

            var prog = TestData.Program("a", installPath: dir, checksum: "sha256:" + hash);  // same version, same checksum
            var plan = svc.BuildPlan(
                TestData.Manifest(prog),                                                     // remote
                TestData.Manifest(TestData.Program("a", installPath: dir, checksum: "sha256:" + hash)),  // local (matches)
                new Dictionary<string, string> { ["a"] = hash });                            // cache says the fingerprint matches

            var action = Assert.Single(plan.Actions);              // one action
            Assert.Equal(SyncActionType.UpToDate, action.Type);    // nothing to do
            Assert.False(plan.HasWork);                            // plan reports no work
        }
        finally { Directory.Delete(dir, recursive: true); }        // clean up the temp folder
    }

    [Fact]
    public void SameVersion_MissingFile_IsRepairedAsInstall()
    {
        var svc = NewService();
        var dir = Path.Combine(Path.GetTempPath(), "orch-missing-" + Guid.NewGuid().ToString("N"));  // a folder that does NOT exist
        var prog = TestData.Program("a", installPath: dir, checksum: "sha256:ABCD");                  // same version, but file is missing

        var plan = svc.BuildPlan(
            TestData.Manifest(prog),
            TestData.Manifest(TestData.Program("a", installPath: dir, checksum: "sha256:ABCD")),
            new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);              // one action
        Assert.Equal(SyncActionType.Install, action.Type);     // repaired by reinstalling
    }

    [Fact]
    public void StatusDeleted_WhenInstalled_IsPlannedAsDelete()
    {
        var svc = NewService();
        var local = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Active));    // we had it installed
        var remote = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Deleted));  // remote marks it deleted

        var plan = svc.BuildPlan(remote, local, new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);              // one action
        Assert.Equal(SyncActionType.Delete, action.Type);      // a Delete
    }

    [Fact]
    public void StatusDeleted_NeverInstalled_IsNoOp()
    {
        var svc = NewService();
        var remote = TestData.Manifest(   // remote marks it deleted, but we never had it and its folder doesn't exist
            TestData.Program("a", status: ProgramStatus.Deleted, installPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        var plan = svc.BuildPlan(remote, local: null, new Dictionary<string, string>());

        Assert.Empty(plan.Actions);   // nothing to delete -> no actions at all
    }

    [Fact]
    public void RemovedFromManifestEntirely_IsPlannedAsDelete()
    {
        var svc = NewService();
        var local = TestData.Manifest(TestData.Program("gone", status: ProgramStatus.Active));  // we had "gone" installed
        var remote = TestData.Manifest(TestData.Program("other"));                              // remote dropped it and added "other"

        var plan = svc.BuildPlan(remote, local, new Dictionary<string, string>());

        Assert.Contains(plan.Actions, a => a.Type == SyncActionType.Delete && a.Program.Id == "gone");     // "gone" gets deleted
        Assert.Contains(plan.Actions, a => a.Type == SyncActionType.Install && a.Program.Id == "other");   // "other" gets installed
    }

    [Fact]
    public void StatusDeleted_IsNotDoubleCountedByRemovalSweep()
    {
        var svc = NewService();
        var local = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Active));    // installed
        var remote = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Deleted));  // marked deleted (still present in manifest)

        var plan = svc.BuildPlan(remote, local, new Dictionary<string, string>());

        Assert.Single(plan.Actions, a => a.Type == SyncActionType.Delete && a.Program.Id == "a");  // exactly one Delete, not two
    }
}
