namespace Orchestrator.Service.Models;

/// <summary>Kind of action the sync engine will take for a program.</summary>
public enum SyncActionType
{
    Install,
    Update,
    Delete,
    UpToDate
}

/// <summary>One planned action against a program.</summary>
public sealed class SyncAction
{
    public SyncActionType Type { get; init; }
    public required ProgramEntry Program { get; init; }

    /// <summary>Previously installed version, when known (for Update/Delete).</summary>
    public string? PreviousVersion { get; init; }

    public override string ToString() => $"{Type}: {Program.Name} v{Program.Version}";
}

/// <summary>Computed plan comparing remote manifest against local state.</summary>
public sealed class SyncPlan
{
    public List<SyncAction> Actions { get; } = new();

    public IEnumerable<SyncAction> Installs => Actions.Where(a => a.Type == SyncActionType.Install);
    public IEnumerable<SyncAction> Updates => Actions.Where(a => a.Type == SyncActionType.Update);
    public IEnumerable<SyncAction> Deletes => Actions.Where(a => a.Type == SyncActionType.Delete);

    public bool HasWork => Actions.Any(a => a.Type != SyncActionType.UpToDate);
}
