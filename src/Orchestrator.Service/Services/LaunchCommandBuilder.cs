// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Works out the exact command line needed to launch a program, based on its type.
//   A .ps1 needs powershell.exe with some flags; a .bat needs cmd.exe /c; a .py needs
//   pythonw; a plain .exe just runs itself. This is pure "figure out the string" code
//   with no side effects, so it can feed both the registry Run entry and the Scheduled
//   Task, and be unit-tested even on a Mac.
// =====================================================================================

using Orchestrator.Service.Models;   // for ProgramEntry / ProgramType

namespace Orchestrator.Service.Services;   // groups this with the other services

/// <summary>
/// Builds the OS command line used to launch a program, per program type.
/// Pure and platform-agnostic so it can back both the HKLM Run value and the
/// Scheduled Task action, and be unit-tested off Windows.
/// </summary>
public static class LaunchCommandBuilder   // static = pure helper, no instance/state
{
    /// <summary>Path to the interpreter host, split from its arguments.</summary>
    public readonly record struct Command(string Executable, string Arguments);  // a simple (exe, args) pair

    // Interpreter locations. Registry/Task both accept full paths; keep these
    // resolvable on Windows and harmless (used only as strings) elsewhere.
    public static string PowerShellPath =>                                 // full path to the built-in powershell.exe
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),   // the Windows System32 folder
            "WindowsPowerShell", "v1.0", "powershell.exe");                // + the standard PowerShell sub-path

    /// <summary>Decompose a program into the executable to run and its argument string.</summary>
    public static Command Build(ProgramEntry program)
    {
        var file = program.FullFilePath;   // full path to the installed file
        var extra = string.IsNullOrWhiteSpace(program.Arguments) ? string.Empty : " " + program.Arguments.Trim();  // any extra args, with a leading space

        return program.Type switch   // pick the right launcher based on the program type
        {
            ProgramType.PowerShell => new Command(                          // .ps1 -> run via powershell.exe...
                PowerShellPath,
                $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{file}\"{extra}"),  // ...with safe, hidden, no-profile flags
            ProgramType.Python => new Command("pythonw", $"\"{file}\"{extra}"),   // .py -> run via pythonw (no console window)
            ProgramType.Batch => new Command("cmd.exe", $"/c \"{file}\"{extra}"), // .bat -> run via cmd.exe /c
            ProgramType.Vbs => new Command("wscript.exe", $"\"{file}\"{extra}"),  // .vbs -> run via wscript.exe
            _ /* Exe */ => new Command(file, extra.TrimStart())                   // plain exe -> just run the file itself
        };
    }

    /// <summary>
    /// The single-string command line written to an HKLM Run value:
    /// <c>"exe" args</c> (exe quoted only when it looks like a path).
    /// </summary>
    public static string BuildRunKeyValue(ProgramEntry program)
    {
        var cmd = Build(program);                                     // get the (exe, args) pair
        var exe = cmd.Executable.Contains(' ') || cmd.Executable.Contains('\\')  // if the exe has a space or looks like a path...
            ? $"\"{cmd.Executable}\""                                 // ...wrap it in quotes so Windows parses it correctly
            : cmd.Executable;                                        // otherwise leave it as-is
        return string.IsNullOrEmpty(cmd.Arguments) ? exe : $"{exe} {cmd.Arguments}";  // join exe + args into one command line
    }
}
