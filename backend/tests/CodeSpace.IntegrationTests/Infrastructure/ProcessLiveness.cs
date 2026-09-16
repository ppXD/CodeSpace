using System.Diagnostics;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// The liveness oracle the PRODUCT uses, mirrored for tests that assert a kill happened.
///
/// <para>The tests that need it launch a supervised agent under the durable runner and then ask whether the reconciler
/// killed it. The supervised pid is a GRANDCHILD of the test process — the broker's child, not xunit's — so nothing in
/// the test process ever <c>wait()</c>s on it, and on Linux a killed grandchild sits as a ZOMBIE (<c>/proc/pid/stat</c>
/// state <c>Z</c>) until its own reaper gets to it. <see cref="Process.HasExited"/> on a non-child reports a zombie as
/// STILL RUNNING, while <c>NativeProcess.IsAlive</c> — the product's own oracle, which decides whether the runner
/// reports a kill as effective — treats <c>Z</c>/<c>X</c> as gone. Those two answers disagreeing is why
/// <c>Alive_durable_run_past_the_reattach_ceiling_is_killed_then_abandoned</c> went red on Linux CI while the run it
/// was checking really had been killed and abandoned.</para>
///
/// <para>MEASURED, not assumed, against a real zombie (a child backgrounded by a shell that then stops itself, so it
/// can never reap): on LINUX <c>Process.HasExited</c> reports the corpse as still running, while the product counts it
/// gone — on macOS the same call goes through libproc and agrees with the product. That asymmetry is the whole reason
/// the CI red was Linux-only while a macOS dev box stayed green, and it is why a local green for any kill assertion is
/// weaker evidence than the CI one. The experiment is not kept as a test: producing the corpse needs a STOPPED shell,
/// and a stopped process never closes the std pipes it inherits, which hangs the whole run with nothing named.</para>
///
/// <para>Mirror rule (Rule 12.5): <c>ProcessLivenessDriftTests</c> fails if the product's dead-state set moves and this
/// file does not. Never widen this to something more convenient than the product — a test that is more generous than
/// the thing it measures stops measuring it.</para>
/// </summary>
internal static class ProcessLiveness
{
    /// <summary>The <c>/proc/pid/stat</c> states the product counts as DEAD: <c>Z</c> (zombie, exited and awaiting reap) and <c>X</c> (dead). Pinned against the product's own literal by the drift detector.</summary>
    internal static readonly string[] DeadLinuxStates = ["Z", "X"];

    /// <summary>The product refuses to answer for a pid at or below this (<c>NativeProcess.IsAlive</c>: <c>identity.ProcessId &lt;= 1</c>) — init is nobody's supervised agent, and 0 / negative are not pids at all.</summary>
    internal const int LowestAnswerablePid = 2;

    /// <summary>Whether <paramref name="pid"/> is alive BY THE PRODUCT'S DEFINITION — on Linux, present in <c>/proc</c> and not in a dead state; elsewhere, the ordinary managed answer.</summary>
    public static bool IsAliveLikeTheProduct(int pid)
    {
        if (pid < LowestAnswerablePid) return false;
        if (!OperatingSystem.IsLinux()) return IsAliveByManagedHandle(pid);

        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return !DeadLinuxStates.Contains(LinuxState(stat));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The state field of a <c>/proc/pid/stat</c> line, read past the comm field's parentheses exactly as the product reads it (a comm may itself contain spaces and brackets).</summary>
    internal static string LinuxState(string stat) => stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    private static bool IsAliveByManagedHandle(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }

    /// <summary>
    /// Everything that could explain a surprising verdict about <paramref name="pid"/>, for the failure message of any
    /// assertion that waits on a process. A bare "should be True but was False" about liveness is unfixable from a CI
    /// log — the reader cannot tell a corpse from a reaped pid from an unreadable <c>/proc</c>, and those want three
    /// different fixes — so every such assertion states this instead.
    /// </summary>
    public static string Describe(int pid)
    {
        var managed = "?";
        try { using var process = Process.GetProcessById(pid); managed = (!process.HasExited).ToString(); }
        catch (Exception error) { managed = $"threw {error.GetType().Name}"; }

        return $"pid {pid}: mirror={IsAliveLikeTheProduct(pid)} managed(HasExited)={managed} {DescribeProcEntry(pid)} — diagnose by hand with `ps -p {pid} -o pid,ppid,stat,etime,command`";
    }

    private static string DescribeProcEntry(int pid)
    {
        if (!OperatingSystem.IsLinux()) return "(no /proc on this platform)";

        try { var stat = File.ReadAllText($"/proc/{pid}/stat").Trim(); return $"state={LinuxState(stat)} stat=[{stat[..Math.Min(120, stat.Length)]}]"; }
        catch (Exception error) { return $"/proc/{pid}/stat unreadable: {error.GetType().Name}: {error.Message}"; }
    }
}
