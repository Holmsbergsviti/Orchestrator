// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for per-machine targeting. They cover: a program with no target
//   applies everywhere; a program targeted at another machine is filtered out (and, if
//   it was installed here, gets uninstalled); matching works by hostname OR machine id
//   and is case-insensitive; "all" matches everyone; and the string-or-array "target"
//   field parses both ways.
// =====================================================================================

using System.Text.Json;                                // to test target JSON parsing
using Microsoft.Extensions.Logging.Abstractions;       // a no-op logger for the service under test
using Orchestrator.Service.Models;                     // for the model classes
using Orchestrator.Service.Services;                   // the code being tested
using Xunit;                                           // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class TargetingTests
{
    private const string MyId = "11111111-1111-1111-1111-111111111111";   // this fake machine's id
    private const string MyHost = "OLEGS-LAPTOP";                          // this fake machine's hostname

    private static ManifestService NewService()
        => new(new FakeConfigService(), NullLogger<ManifestService>.Instance);

    // ---- AppliesToMachine (the matching rule) ------------------------------------------

    [Fact]
    public void NoTarget_AppliesToEveryMachine()
    {
        var p = TestData.Program("a", target: null);
        Assert.True(p.AppliesToMachine(MyId, MyHost));
    }

    [Theory]
    [InlineData("all")]         // the literal "all" keyword
    [InlineData("olegs-laptop")] // hostname, different case
    [InlineData("11111111-1111-1111-1111-111111111111")] // exact machine id
    public void MatchingTarget_Applies(string token)
    {
        var p = TestData.Program("a", target: new List<string> { token });
        Assert.True(p.AppliesToMachine(MyId, MyHost));
    }

    [Fact]
    public void NonMatchingTarget_DoesNotApply()
    {
        var p = TestData.Program("a", target: new List<string> { "some-other-pc" });
        Assert.False(p.AppliesToMachine(MyId, MyHost));
    }

    [Fact]
    public void ListWithThisMachineAmongOthers_Applies()
    {
        var p = TestData.Program("a", target: new List<string> { "pc-a", MyHost, "pc-c" });
        Assert.True(p.AppliesToMachine(MyId, MyHost));
    }

    // ---- FilterForMachine (turning the manifest into this machine's view) --------------

    [Fact]
    public void FilterForMachine_KeepsTargetedProgramActive()
    {
        var svc = NewService();
        var remote = TestData.Manifest(TestData.Program("a", target: new List<string> { MyHost }));

        var effective = svc.FilterForMachine(remote, MyId, MyHost);

        var prog = Assert.Single(effective.Programs);
        Assert.Equal(ProgramStatus.Active, prog.Status);
    }

    [Fact]
    public void FilterForMachine_MarksUntargetedProgramDeleted()
    {
        var svc = NewService();
        var remote = TestData.Manifest(TestData.Program("a", target: new List<string> { "other-pc" }));

        var effective = svc.FilterForMachine(remote, MyId, MyHost);

        var prog = Assert.Single(effective.Programs);
        Assert.Equal(ProgramStatus.Deleted, prog.Status);   // not for me -> presented as deleted so it uninstalls here
    }

    [Fact]
    public void UntargetedButInstalled_IsPlannedAsDelete()
    {
        var svc = NewService();
        // We previously installed "a" here; now the manifest retargets it to another machine only.
        var local = TestData.Manifest(TestData.Program("a", status: ProgramStatus.Active));
        var remote = TestData.Manifest(TestData.Program("a", target: new List<string> { "other-pc" }));

        var effective = svc.FilterForMachine(remote, MyId, MyHost);
        var plan = svc.BuildPlan(effective, local, new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Delete, action.Type);
        Assert.Equal("a", action.Program.Id);
    }

    [Fact]
    public void UntargetedAndNeverInstalled_IsNoOp()
    {
        var svc = NewService();
        var remote = TestData.Manifest(TestData.Program(
            "a",
            target: new List<string> { "other-pc" },
            installPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));  // folder does not exist

        var effective = svc.FilterForMachine(remote, MyId, MyHost);
        var plan = svc.BuildPlan(effective, local: null, new Dictionary<string, string>());

        Assert.Empty(plan.Actions);   // nothing installed and not for me -> do nothing
    }

    [Fact]
    public void TargetedProgram_IsPlannedAsInstall()
    {
        var svc = NewService();
        var remote = TestData.Manifest(TestData.Program("a", target: new List<string> { MyId }));

        var effective = svc.FilterForMachine(remote, MyId, MyHost);
        var plan = svc.BuildPlan(effective, local: null, new Dictionary<string, string>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Install, action.Type);
    }

    // ---- JSON shape (string OR array) --------------------------------------------------

    [Fact]
    public void Target_ParsesFromSingleString()
    {
        var p = JsonSerializer.Deserialize<ProgramEntry>("""{ "id": "a", "target": "pc-a" }""")!;
        Assert.Equal(new[] { "pc-a" }, p.Target);
    }

    [Fact]
    public void Target_ParsesFromArray()
    {
        var p = JsonSerializer.Deserialize<ProgramEntry>("""{ "id": "a", "target": ["pc-a", "pc-b"] }""")!;
        Assert.Equal(new[] { "pc-a", "pc-b" }, p.Target);
    }

    [Fact]
    public void Target_MissingIsNull()
    {
        var p = JsonSerializer.Deserialize<ProgramEntry>("""{ "id": "a" }""")!;
        Assert.Null(p.Target);
    }
}
