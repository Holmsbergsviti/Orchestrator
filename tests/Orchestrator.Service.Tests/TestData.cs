// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Shared helpers for the tests. Instead of every test building a full ProgramEntry
//   or Manifest by hand, they call these little factory methods to get a ready-made
//   sample with sensible defaults. It also has a fake ConfigService so tests can run
//   without touching real files or the real machine.
// =====================================================================================

using Orchestrator.Service.Models;     // for ProgramEntry / Manifest / config
using Orchestrator.Service.Services;   // for IConfigService

namespace Orchestrator.Service.Tests;   // groups this with the other tests

internal static class TestData   // static helper (no instances needed)
{
    public static ProgramEntry Program(   // build a sample ProgramEntry with defaults you can override
        string id,
        string version = "1.0",
        ProgramStatus status = ProgramStatus.Active,
        string installPath = "",
        string fileName = "app.exe",
        ProgramType type = ProgramType.Exe,
        string? checksum = null,
        bool runAtStartup = false,
        bool runAsAdmin = false,
        string? arguments = null,
        string? description = null,
        List<string>? target = null)
        => new()   // create and populate the entry from the arguments above
        {
            Id = id,                       // id and name both default to the given id
            Name = id,
            Version = version,
            Status = status,
            InstallPath = installPath,
            FileName = fileName,
            Type = type,
            Checksum = checksum,
            RunAtStartup = runAtStartup,
            RunAsAdmin = runAsAdmin,
            Arguments = arguments,
            Description = description,
            Target = target               // which machines it applies to (null = all)
        };

    public static Manifest Manifest(params ProgramEntry[] programs)   // build a manifest from a list of programs
        => new() { Programs = programs.ToList() };
}

/// <summary>Minimal IConfigService for tests that only need <see cref="OrchestratorConfig"/>.</summary>
internal sealed class FakeConfigService : IConfigService   // a stand-in config service used by tests
{
    public OrchestratorConfig Config { get; } = new();               // default settings
    public MachineConfig LoadOrCreateMachineConfig() => new();       // return a blank machine config
    public void SaveMachineConfig(MachineConfig config) { }          // do nothing (no disk in tests)
    public void EnsureDirectories() { }                              // do nothing (no folders needed)
}
