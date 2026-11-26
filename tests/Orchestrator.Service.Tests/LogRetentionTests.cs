// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks the cleanup of the agent's own log folder: old days go, then the oldest until the
//   size fits, the newest file and the other json files stay, and a locked file is skipped.
// =====================================================================================

using Orchestrator.Service.Services;   // LogRetention
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;

public class LogRetentionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("logret").FullName;   // a throwaway logs folder
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Make(string name, int daysOld, int bytes = 10)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public void DeletesFilesOlderThanTheLimit_KeepsTheRest()
    {
        var today = Make("log-20261126.txt", 0);
        var recent = Make("log-20261120.txt", 6);
        var old = Make("log-20261101.txt", 25);

        Assert.Equal(1, LogRetention.Prune(_dir, 14, 0, Now));

        Assert.True(File.Exists(today));
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void WhenOverTheSizeCap_DropsOldestFirstUntilItFits()
    {
        var newest = Make("log-20261126.txt", 0, 100);
        var mid = Make("log-20261125.txt", 1, 100);
        var oldest = Make("log-20261124.txt", 2, 100);

        Assert.Equal(1, LogRetention.Prune(_dir, 14, 250, Now));   // 300 > 250: the oldest goes, 200 fits

        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(mid));
        Assert.False(File.Exists(oldest));
    }

    [Fact]
    public void NewestFileIsNeverDeleted_EvenWhenItAloneIsOverTheCap()
    {
        var newest = Make("log-20261126.txt", 0, 500);

        Assert.Equal(0, LogRetention.Prune(_dir, 1, 100, Now));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void LeavesOtherFilesAlone()
    {
        Make("log-20261126.txt", 0);
        var history = Make("sync-history.json", 90);
        var audit = Make("remote-sessions.json", 90);

        LogRetention.Prune(_dir, 1, 1, Now);

        Assert.True(File.Exists(history));
        Assert.True(File.Exists(audit));
    }

    [Fact]
    public void ZeroSwitchesARuleOff()
    {
        Make("log-20261126.txt", 0, 100);
        var old = Make("log-20250101.txt", 400, 100);

        Assert.Equal(0, LogRetention.Prune(_dir, 0, 0, Now));
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void MissingFolder_IsFine()
        => Assert.Equal(0, LogRetention.Prune(Path.Combine(_dir, "nope"), 14, 1000, Now));

    [Fact]
    public void ALockedFileIsSkipped_NotAnError()
    {
        Make("log-20261126.txt", 0);
        var locked = Make("log-20261001.txt", 56);
        var other = Make("log-20261002.txt", 55);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var removed = LogRetention.Prune(_dir, 14, 0, Now);

        Assert.False(File.Exists(other));
        // Windows refuses to delete an open file, so it survives there; other systems allow it, so only check it where it holds.
        if (OperatingSystem.IsWindows()) { Assert.True(File.Exists(locked)); Assert.Equal(1, removed); }
    }
}
