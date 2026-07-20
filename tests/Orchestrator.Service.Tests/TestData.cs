using Orchestrator.Service.Models;
using Orchestrator.Service.Services;

namespace Orchestrator.Service.Tests;

internal static class TestData
{
    public static ProgramEntry Program(
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
        string? description = null)
        => new()
        {
            Id = id,
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
            Description = description
        };

    public static Manifest Manifest(params ProgramEntry[] programs)
        => new() { Programs = programs.ToList() };
}

/// <summary>Minimal IConfigService for tests that only need <see cref="OrchestratorConfig"/>.</summary>
internal sealed class FakeConfigService : IConfigService
{
    public OrchestratorConfig Config { get; } = new();
    public MachineConfig LoadOrCreateMachineConfig() => new();
    public void SaveMachineConfig(MachineConfig config) { }
    public void EnsureDirectories() { }
}
