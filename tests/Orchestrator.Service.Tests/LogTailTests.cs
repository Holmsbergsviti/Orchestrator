// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks the piece of the heartbeat that picks which part of the agent's log gets sent to
//   GitHub: the newest log file, only its end, never more than the size cap, and readable
//   even while another process still has the file open for writing.
// =====================================================================================

using Orchestrator.Service.Services;   // FleetReporter.ReadLogTail
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;

public class LogTailTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("logtail").FullName;   // a throwaway logs folder

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NoLogFolderOrNoLogFile_ReturnsNull()
    {
        Assert.Null(FleetReporter.ReadLogTail(Path.Combine(_dir, "missing")));
        Assert.Null(FleetReporter.ReadLogTail(_dir));   // folder exists but is empty
    }

    [Fact]
    public void PicksNewestFile_AndOnlyTheLastLines()
    {
        var old = Path.Combine(_dir, "log-20261124.txt");
        File.WriteAllText(old, "old line\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(_dir, "log-20261125.txt"), string.Join("\n", Enumerable.Range(1, 1000).Select(i => $"line {i}")));

        var text = FleetReporter.ReadLogTail(_dir, maxLines: 5)!;

        Assert.StartsWith("# log-20261125.txt", text);
        Assert.Contains("line 1000", text);
        Assert.DoesNotContain("line 990", text);
        Assert.DoesNotContain("old line", text);
    }

    [Fact]
    public void BigFile_IsCutToTheByteCap_WithoutAHalfLine()
    {
        File.WriteAllText(Path.Combine(_dir, "log-1.txt"), string.Concat(Enumerable.Range(1, 5000).Select(i => $"line number {i}\n")));

        var text = FleetReporter.ReadLogTail(_dir, maxLines: 100000, maxBytes: 2000)!;

        Assert.True(text.Length < 2500);
        Assert.Contains("line number 5000", text);
        Assert.All(text.Split('\n').Skip(1).Where(l => l.Length > 0), l => Assert.StartsWith("line number ", l));   // no cut-off first line
    }

    [Fact]
    public void FileStillOpenForWriting_CanBeRead()
    {
        var path = Path.Combine(_dir, "log-1.txt");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);   // like Serilog's shared writer
        writer.Write("hello\nworld\n"u8);
        writer.Flush();

        Assert.Contains("world", FleetReporter.ReadLogTail(_dir)!);
    }
}
