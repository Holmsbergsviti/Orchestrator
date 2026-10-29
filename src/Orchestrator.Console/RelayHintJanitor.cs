// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Background tidy-up for the throwaway "where to dial" hint (see ControlRepo): every few
//   seconds it checks whether any live-screen session the console published an address for has
//   finished (or never started), and if so takes it out of the hint — deleting the hint branch
//   from GitHub once nothing is left. On startup it also clears whatever a previous run left
//   behind. When no session is open it does no network work at all.
// =====================================================================================

namespace Orchestrator.Console;

public sealed class RelayHintJanitor(ControlRepo repo, ILogger<RelayHintJanitor> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await repo.NoteStartupAsync(ct);   // a crashed run may have left a hint on GitHub
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await repo.SweepRelayHintAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "relay hint sweep failed"); }   // never let the loop die
        }
        while (await WaitAsync(timer, ct));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
