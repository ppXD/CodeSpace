using System.Diagnostics;
using CodeSpace.Messages.Agents;

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
    internal static string LinuxState(string stat) => FieldsAfterComm(stat)[0];

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

    /// <summary>
    /// Everything above about a LAUNCHED handle's pid, plus the birth key the product compares it against, plus what
    /// the bootstrap that launched it said. Three different questions — what this pid is now, whether the product
    /// still recognises it as OURS, and how it got that way — and a red carrying only the first is the shape this
    /// family kept reappearing in. The birth key earns its place because a LIVE process whose recorded key no longer
    /// matches field 22 of the stat above is reported Gone by the product, which reads identically to a dead one.
    /// </summary>
    public static string Describe(SandboxHandle handle) =>
        $"{Describe(handle.ProcessId)}\nrecorded birth key {handle.NativeLaunch?.Execution?.StartKey ?? "(none: a legacy handle)"} — field 22 of the stat above is the live one\n{DescribeBootstrap(handle.SpoolDirectory)}";

    /// <summary>
    /// The TAIL of what the bootstrap itself said about this launch (<c>launch-v1/bootstrap.err</c> under
    /// <paramref name="spoolDirectory"/>). A dead pid on its own cannot distinguish "the broker refused to release
    /// this execution" from "the kernel killed it" from "execve failed" — the bootstrap names which, and a failure
    /// message that omits it sends the reader to a CI log that no longer exists.
    /// </summary>
    public static string DescribeBootstrap(string? spoolDirectory)
    {
        if (string.IsNullOrEmpty(spoolDirectory)) return "no spool directory on the handle, so the bootstrap's own diagnostics cannot be located";
        var path = Path.Combine(spoolDirectory, NativeLaunchProtocol.DirectoryName, NativeLaunchProtocol.DiagnosticsFile);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - BootstrapTailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            // Past the first newline: a byte-arbitrary cut lands mid-character and the tail opens with replacement
            // marks. Whole lines only, unless the whole file is inside the budget.
            if (stream.Length > BootstrapTailBytes) reader.ReadLine();
            var tail = reader.ReadToEnd().Trim();
            return tail.Length == 0 ? $"{path} is empty: the bootstrap died without a word" : $"the bootstrap said:\n{tail}";
        }
        catch (FileNotFoundException) { return $"{path} was never written: this launch had no bootstrap, or it died before it could open one"; }
        catch (Exception error) { return $"{path} unreadable: {error.GetType().Name}"; }
    }

    /// <summary>Enough of the tail to carry the last few refusals without pasting a whole run's stderr into an assertion message.</summary>
    private const int BootstrapTailBytes = 4096;

    private static string DescribeProcEntry(int pid)
    {
        if (!OperatingSystem.IsLinux()) return "(no /proc on this platform)";

        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat").Trim();
            var fields = FieldsAfterComm(stat);
            return $"comm={Comm(stat)} state={fields[0]} {DescribeParent(fields)} {DescribeExitCode(fields)} stat=[{stat[..Math.Min(120, stat.Length)]}]";
        }
        catch (Exception error) { return $"/proc/{pid}/stat unreadable: {error.GetType().Name}: {error.Message}"; }
    }

    /// <summary>Who has not reaped this corpse. A supervised pid whose parent is the LAUNCHING test host died before it ever became the workload; one whose parent is the bootstrap died under its own broker — different defects, indistinguishable from the pid alone.</summary>
    private static string DescribeParent(string[] fields)
    {
        if (fields.Length < 2 || !int.TryParse(fields[1], out var parent)) return "ppid=?";

        try { return $"ppid={parent} ({File.ReadAllText($"/proc/{parent}/comm").Trim()})"; }
        catch (Exception error) { return $"ppid={parent} (comm unreadable: {error.GetType().Name})"; }
    }

    /// <summary>
    /// Field 52 of <c>/proc/pid/stat</c> — the thread's exit status in <c>waitpid</c> form — decoded. The kernel only
    /// fills it in for a corpse, so it is reported only for one: for anything still running it is a stale 0 that would
    /// read as a clean exit. This is the field that separates "the workload refused itself" (exit 125/126) from "something
    /// killed it" (signal 9), which no amount of staring at a zombie's state letter can.
    /// </summary>
    private static string DescribeExitCode(string[] fields)
    {
        if (!DeadLinuxStates.Contains(fields[0])) return "exit=(still running)";
        if (fields.Length < 50 || !int.TryParse(fields[49], out var status)) return "exit=(the kernel did not report one)";

        return "exit=" + DescribeWaitStatus(status);
    }

    /// <summary>A managed <see cref="Process.ExitCode"/> in words. On Unix .NET reports a signalled child as 128 + the signal, so a bare "134" reads as an exit status when it is really SIGABRT — which for a .NET child is its own unhandled exception, a completely different investigation from a kill.</summary>
    public static string DescribeManagedExitCode(int exitCode) =>
        exitCode is > 128 and < 192 ? $"{exitCode}: killed by signal {exitCode - 128} ({SignalName(exitCode - 128)})" : exitCode.ToString();

    /// <summary>A <c>waitpid</c> status word in words: an exit code, or the signal that ended it.</summary>
    internal static string DescribeWaitStatus(int status)
    {
        var signal = status & 0x7f;
        if (signal == 0) return $"exited({(status >> 8) & 0xff})";
        if (signal == 0x7f) return "stopped";

        return $"killed by signal {signal} ({SignalName(signal)}){((status & 0x80) != 0 ? ", core dumped" : "")}";
    }

    private static string SignalName(int signal) => signal switch
    {
        1 => "SIGHUP", 2 => "SIGINT", 4 => "SIGILL", 6 => "SIGABRT", 7 => "SIGBUS", 8 => "SIGFPE",
        9 => "SIGKILL", 11 => "SIGSEGV", 13 => "SIGPIPE", 15 => "SIGTERM", 24 => "SIGXCPU", 25 => "SIGXFSZ",
        _ => "unnamed here",
    };

    /// <summary>The comm field, brackets stripped — truncated to 15 characters by the kernel, so <c>codespace-runne</c> IS the bootstrap.</summary>
    internal static string Comm(string stat) => stat[(stat.IndexOf('(') + 1)..stat.LastIndexOf(')')];

    /// <summary>Everything from the state field on, read past the comm's parentheses exactly as the product reads it.</summary>
    internal static string[] FieldsAfterComm(string stat) => stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
