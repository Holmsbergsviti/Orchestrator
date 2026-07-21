// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The "to-do list" the service builds each cycle by comparing what GitHub says
//   should be installed against what's actually on the machine. A SyncPlan is a list
//   of SyncActions, and each SyncAction says "install / update / delete / leave alone"
//   for one program. The rest of the code then just walks the plan and carries it out.
// =====================================================================================

namespace Orchestrator.Service.Models;   // groups this with the other data models

/// <summary>Kind of action the sync engine will take for a program.</summary>
public enum SyncActionType   // the four possible decisions for a program
{
    Install,    // it's new (or missing/corrupt) -> download and install
    Update,     // the version changed -> replace it
    Delete,     // it should be removed -> uninstall it
    UpToDate    // nothing to do -> already correct
}

/// <summary>One planned action against a program.</summary>
public sealed class SyncAction
{
    public SyncActionType Type { get; init; }              // which of the four actions this is
    public required ProgramEntry Program { get; init; }    // the program the action applies to (must be provided)

    /// <summary>Previously installed version, when known (for Update/Delete).</summary>
    public string? PreviousVersion { get; init; }          // the old version, for nicer log messages

    public override string ToString() => $"{Type}: {Program.Name} v{Program.Version}";  // readable text for logs
}

/// <summary>Computed plan comparing remote manifest against local state.</summary>
public sealed class SyncPlan
{
    public List<SyncAction> Actions { get; } = new();      // every decision made this cycle

    public IEnumerable<SyncAction> Installs => Actions.Where(a => a.Type == SyncActionType.Install);  // shortcut: just the installs
    public IEnumerable<SyncAction> Updates => Actions.Where(a => a.Type == SyncActionType.Update);    // shortcut: just the updates
    public IEnumerable<SyncAction> Deletes => Actions.Where(a => a.Type == SyncActionType.Delete);    // shortcut: just the deletes

    public bool HasWork => Actions.Any(a => a.Type != SyncActionType.UpToDate);  // true if anything actually needs doing
}
