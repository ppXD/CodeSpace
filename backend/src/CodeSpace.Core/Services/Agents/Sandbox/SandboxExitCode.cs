using System.Globalization;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Human-readable description of a sandboxed process exit code, DECODING the POSIX <c>128+signal</c> convention. A
/// shell — and the durable spool's <c>exit</c> marker, which records <c>$?</c> of the wrapper chain — reports a
/// signal-terminated child as <c>128+signum</c>, so a bare "exited with code 137" is really "killed by SIGKILL (9)":
/// almost always an OOM or a resource-limit kill (<c>RLIMIT_NPROC</c> / <c>RLIMIT_FSIZE</c> / <c>RLIMIT_CPU</c>), NOT
/// an application error. A code of <c>-1</c> is the durable runner's "process vanished with no exit marker" sentinel
/// (a whole-tree SIGKILL / host teardown). Surfacing the signal name makes a runner-side kill self-explanatory in
/// <c>AgentRun.Error</c> + the real-model verdict note, instead of an opaque number a reader must decode by hand.
/// </summary>
public static class SandboxExitCode
{
    /// <summary>
    /// "<c>{code}</c>" for a normal exit; "<c>{code} (terminated by signal SIG{NAME}/{n} — …)</c>" when
    /// <paramref name="exitCode"/> is <c>128+signal</c>; a vanished-process note for the <c>-1</c> sentinel.
    /// </summary>
    public static string Describe(int exitCode)
    {
        if (exitCode < 0)
            return $"{Num(exitCode)} (no exit marker — the process vanished, likely an external/OOM kill or host teardown)";

        // 128+signum is the shell/wait convention for a signal-terminated child; signals run 1..64 (std + realtime).
        if (exitCode > 128 && exitCode <= 128 + 64)
        {
            var signum = exitCode - 128;
            var name = SignalName(signum);

            return name is null
                ? $"{Num(exitCode)} (terminated by signal {signum} — likely an OOM or resource-limit kill, not an application error)"
                : $"{Num(exitCode)} (terminated by signal {name}/{signum} — likely an OOM or resource-limit kill, not an application error)";
        }

        return Num(exitCode);
    }

    private static string Num(int code) => code.ToString(CultureInfo.InvariantCulture);

    /// <summary>The common POSIX signal names a sandboxed agent can realistically be killed by; null → render the bare number.</summary>
    private static string? SignalName(int signum) => signum switch
    {
        2 => "SIGINT",
        6 => "SIGABRT",
        9 => "SIGKILL",
        11 => "SIGSEGV",
        13 => "SIGPIPE",
        15 => "SIGTERM",
        24 => "SIGXCPU",
        25 => "SIGXFSZ",
        _ => null,
    };
}
