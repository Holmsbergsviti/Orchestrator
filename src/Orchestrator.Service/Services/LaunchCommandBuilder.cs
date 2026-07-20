using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

/// <summary>
/// Builds the OS command line used to launch a program, per program type.
/// Pure and platform-agnostic so it can back both the HKLM Run value and the
/// Scheduled Task action, and be unit-tested off Windows.
/// </summary>
public static class LaunchCommandBuilder
{
    /// <summary>Path to the interpreter host, split from its arguments.</summary>
    public readonly record struct Command(string Executable, string Arguments);

    // Interpreter locations. Registry/Task both accept full paths; keep these
    // resolvable on Windows and harmless (used only as strings) elsewhere.
    public static string PowerShellPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Decompose a program into the executable to run and its argument string.</summary>
    public static Command Build(ProgramEntry program)
    {
        var file = program.FullFilePath;
        var extra = string.IsNullOrWhiteSpace(program.Arguments) ? string.Empty : " " + program.Arguments.Trim();

        return program.Type switch
        {
            ProgramType.PowerShell => new Command(
                PowerShellPath,
                $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{file}\"{extra}"),
            ProgramType.Python => new Command("pythonw", $"\"{file}\"{extra}"),
            ProgramType.Batch => new Command("cmd.exe", $"/c \"{file}\"{extra}"),
            ProgramType.Vbs => new Command("wscript.exe", $"\"{file}\"{extra}"),
            _ /* Exe */ => new Command(file, extra.TrimStart())
        };
    }

    /// <summary>
    /// The single-string command line written to an HKLM Run value:
    /// <c>"exe" args</c> (exe quoted only when it looks like a path).
    /// </summary>
    public static string BuildRunKeyValue(ProgramEntry program)
    {
        var cmd = Build(program);
        var exe = cmd.Executable.Contains(' ') || cmd.Executable.Contains('\\')
            ? $"\"{cmd.Executable}\""
            : cmd.Executable;
        return string.IsNullOrEmpty(cmd.Arguments) ? exe : $"{exe} {cmd.Arguments}";
    }
}
