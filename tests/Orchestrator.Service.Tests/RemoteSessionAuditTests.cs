// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the remote-control audit trail. They are about the bounded ring: an
//   audit log that silently drops the newest entry, or grows without limit, is worse than none.
// =====================================================================================

using System.Text.Json;                // round-tripping the audit file
using Orchestrator.Service.Models;     // the code being tested
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;

public sealed class RemoteSessionAuditTests
{
    private static RemoteSessionRecord Record(string id) => new()
    {
        SessionId = id,
        MachineId = "machine-1",
        Hostname = "PC-1",
        StartedUtc = DateTimeOffset.UtcNow.ToString("O"),
        EndedUtc = DateTimeOffset.UtcNow.ToString("O"),
        Outcome = "operator"
    };

    [Fact]
    public void Append_KeepsRecordsInOrder()
    {
        var audit = new RemoteSessionAudit();
        audit.Append(Record("a"));
        audit.Append(Record("b"));

        Assert.Equal(new[] { "a", "b" }, audit.Records.Select(r => r.SessionId));
    }

    [Fact]
    public void Append_DropsOldestOnceFull_AndAlwaysKeepsTheNewest()
    {
        var audit = new RemoteSessionAudit();
        for (var i = 0; i < RemoteSessionAudit.MaxRecords + 25; i++)
            audit.Append(Record($"s{i}"));

        Assert.Equal(RemoteSessionAudit.MaxRecords, audit.Records.Count);
        // The most recent session must survive — that's the one anyone asking about a session
        // is asking about.
        Assert.Equal($"s{RemoteSessionAudit.MaxRecords + 24}", audit.Records[^1].SessionId);
        Assert.Equal("s25", audit.Records[0].SessionId);   // and the oldest 25 rolled off
    }

    [Fact]
    public void Record_RoundTripsThroughJson()
    {
        var audit = new RemoteSessionAudit();
        var original = Record("round-trip");
        original.DurationSeconds = 12.5;
        original.InputEvents = 42;
        original.Renewals = 2;
        audit.Append(original);

        var restored = JsonSerializer.Deserialize<RemoteSessionAudit>(JsonSerializer.Serialize(audit));

        Assert.NotNull(restored);
        var r = Assert.Single(restored!.Records);
        Assert.Equal("round-trip", r.SessionId);
        Assert.Equal(12.5, r.DurationSeconds);
        Assert.Equal(42, r.InputEvents);
        Assert.Equal(2, r.Renewals);
        Assert.Equal("operator", r.Outcome);
    }
}
