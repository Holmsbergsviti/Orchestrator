// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Starts one of the agent's own helper processes (a screenshot, a live remote-control
//   session) on the DESKTOP of whoever is logged in at the PC — without needing that
//   person's permission to run the agent.
//
//   Why this exists: the agent's folder is locked to SYSTEM and Administrators (it holds the
//   GitHub token). The old way — a Task Scheduler task "run as the logged-in user" — therefore
//   failed with "access denied" for any ordinary user account, so those PCs could never be
//   screenshotted or controlled. Here the service, which runs as SYSTEM, copies its own
//   identity (token), points the copy at the user's session, and starts the helper there.
//   The helper runs as SYSTEM but is drawn on the user's screen, so the capture and the banner
//   work for every kind of account, and nothing in the locked folder is opened up to anyone.
//
//   Two deliberate choices:
//     * The helper gets SYSTEM's own environment, not the user's. Its self-extracting exe would
//       otherwise unpack into the USER's temp folder, where the user could plant a file for a
//       SYSTEM process to load.
//     * It is killed when the allotted time is up, like the scheduled task's time limit was.
//   If this fails, the caller falls back to the old scheduled-task route.
//
//   Nobody signed in (the login screen, e.g. after a restart) still counts: the screen is
//   drawn in the console session by its own protected desktop ("Winlogon"), so the helper is
//   started there and attaches itself to whichever desktop is showing (see InputDesktop).
// =====================================================================================

using System.ComponentModel;                // Win32Exception
using System.Diagnostics;                   // Process: wait for exit, kill on timeout
using System.Runtime.InteropServices;       // P/Invoke into advapi32 / wtsapi32 / userenv
using System.Runtime.Versioning;            // [SupportedOSPlatform("windows")]
using System.Text;                          // StringBuilder for the command line
using Microsoft.Extensions.Logging;         // logging

namespace Orchestrator.Service.Services;

/// <summary>What happened when we tried to start a helper on the user's desktop.</summary>
/// <param name="Started">True when the process was created.</param>
/// <param name="NobodyLoggedOn">True when there is no screen at all to show anything on (no console session exists yet), so a fallback can't help either.</param>
/// <param name="Reason">Plain-language reason when it did not start.</param>
public readonly record struct SessionLaunchResult(bool Started, bool NobodyLoggedOn, string? Reason);

[SupportedOSPlatform("windows")]
public static class UserSessionLauncher
{
    /// <summary>The command line for the helper: the exe in quotes (the path can contain spaces),
    /// then the arguments exactly as given. Pure, so it is unit-tested.</summary>
    public static string BuildCommandLine(string exe, string arguments)
        => string.IsNullOrWhiteSpace(arguments) ? $"\"{exe}\"" : $"\"{exe}\" {arguments}";

    /// <summary>The desktop to start the helper on: the user's normal one when someone is signed in, the
    /// login screen's own protected desktop when nobody is. (It follows the screen from there; see InputDesktop.)
    /// Pure, so it is unit-tested.</summary>
    public static string DesktopFor(bool userSignedIn) => userSignedIn ? "winsta0\\default" : "winsta0\\winlogon";

    /// <summary>Start <paramref name="exe"/> with <paramref name="arguments"/> on the active user's desktop,
    /// or on the login screen when nobody is signed in.
    /// It is killed after <paramref name="killAfter"/>. <paramref name="label"/> names it in the log
    /// (never put secrets in it: the arguments themselves are deliberately not logged).</summary>
    public static SessionLaunchResult TryLaunch(string exe, string arguments, TimeSpan killAfter, string label, ILogger log)
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, "not Windows");

        var sessionId = FindUserSession(out var userName);
        if (sessionId is null)
            return new(false, true, "there is no console session (nothing is on the screen yet)");
        var userSignedIn = userName.Length > 0;
        if (!userSignedIn) userName = "(login screen)";

        IntPtr processToken = IntPtr.Zero, systemToken = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            // 1. A copy of this service's own (SYSTEM) token, as a primary token we may start processes with.
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ALL_ACCESS, out processToken))
                return Fail("could not open the service's token", log, label);
            if (!DuplicateTokenEx(processToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out systemToken))
                return Fail("could not copy the service's token", log, label);

            // 2. Point the copy at the user's session, so the process appears on their screen.
            var session = sessionId.Value;
            if (!SetTokenInformation(systemToken, TokenSessionId, ref session, sizeof(uint)))
                return Fail("could not move the token to the user's session", log, label);

            // 3. SYSTEM's own environment (see the header for why not the user's).
            if (!CreateEnvironmentBlock(out env, systemToken, false)) env = IntPtr.Zero;   // none = inherit; still works

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = DesktopFor(userSignedIn) };
            var cmd = new StringBuilder(BuildCommandLine(exe, arguments));   // CreateProcessAsUser may edit this buffer
            var flags = CREATE_NO_WINDOW | (env != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0u);
            if (!CreateProcessAsUserW(systemToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, env,
                                      Path.GetDirectoryName(exe), ref si, out var pi))
                return Fail($"CreateProcessAsUser failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}", log, label);

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            log.LogInformation("{Label}: started on {User} desktop (session {Session}, process {Pid}), ends within {Minutes} min",
                label, userSignedIn ? userName + "'s" : userName, session, pi.dwProcessId, (int)killAfter.TotalMinutes);
            WatchAsync((int)pi.dwProcessId, killAfter, label, log);
            return new(true, false, null);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, log, label);
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (systemToken != IntPtr.Zero) CloseHandle(systemToken);
            if (processToken != IntPtr.Zero) CloseHandle(processToken);
        }
    }

    private static SessionLaunchResult Fail(string reason, ILogger log, string label)
    {
        log.LogWarning("{Label}: could not start it on the user's desktop ({Reason})", label, reason);
        return new(false, false, reason);
    }

    /// <summary>Log how the helper ended (its exit code is the answer when it dies on startup) and
    /// kill it if it outlives its time limit. Runs in the background; never throws.</summary>
    private static void WatchAsync(int pid, TimeSpan killAfter, string label, ILogger log)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var started = DateTime.UtcNow;
                using var cts = new CancellationTokenSource(killAfter);
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    log.LogWarning("{Label}: still running after its time limit; ending it", label);
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    return;
                }
                log.LogInformation("{Label}: its process ended with exit code {Code} after {Seconds:F0}s",
                    label, proc.ExitCode, (DateTime.UtcNow - started).TotalSeconds);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "{Label}: could not watch the process", label);
            }
        });
    }

    // ---- which session to use ---------------------------------------------------------------

    /// <summary>The console session if someone is signed in there, otherwise the first active
    /// (remote-desktop) session that has a user, otherwise the console session itself (it is showing the
    /// login screen; <paramref name="userName"/> is then empty). Null only when there is no console session.</summary>
    private static uint? FindUserSession(out string userName)
    {
        userName = "";
        var console = WTSGetActiveConsoleSessionId();
        if (console != InvalidSession && QueryUser(console) is { Length: > 0 } consoleUser)
        {
            userName = consoleUser;
            return console;
        }

        if (WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var list, out var count))
        {
            try
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(list + i * size);
                    if (info.State != WTSActive || info.SessionId == 0) continue;   // session 0 is the services' own
                    if (QueryUser((uint)info.SessionId) is { Length: > 0 } user)
                    {
                        userName = user;
                        return (uint)info.SessionId;
                    }
                }
            }
            finally { WTSFreeMemory(list); }
        }
        // Nobody is signed in anywhere: the login screen lives in the console session.
        return console != InvalidSession ? console : null;
    }

    private static string? QueryUser(uint sessionId)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSUserName, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    // ---- Win32 -------------------------------------------------------------------------------

    private const uint InvalidSession = 0xFFFFFFFF;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const int WTSUserName = 5;
    private const int WTSActive = 0;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, uint length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
