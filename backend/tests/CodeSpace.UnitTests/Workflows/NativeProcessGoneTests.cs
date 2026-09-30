using System.ComponentModel;
using System.Diagnostics;
using CodeSpace.NativeLaunch;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// High fidelity (Rule 12) but for one input — the REAL <c>NativeProcess</c> against a REAL reaped child and the REAL
/// test process, the kernel's own answer to <c>kill(pid, 0)</c> deciding every case; only the exception is built by
/// hand, because the race that raises it cannot be staged.
///
/// <para>A process that exits between the <c>/proc</c> directory lookup and the <c>read</c> makes .NET raise a plain
/// <see cref="IOException"/> (ESRCH), and that read is not injectable. So the CLASSIFICATION every liveness reader hands
/// such an exception to is pinned instead — "gone" must never surface as "unknowable" (a poll of a just-killed
/// supervisor threw exactly this once on Linux CI), and "unknowable" must never be folded into "gone" (that would
/// abandon a live run).</para>
///
/// <para><c>Identify</c> asks the runtime for the process AFTER that read, and a process that exits in between is refused
/// there instead: <see cref="Process.GetProcessById(int)"/> throws <see cref="ArgumentException"/> and
/// <see cref="Process.StartTime"/> throws <see cref="Win32Exception"/>. The same classification covers both — the
/// kernel's answer decides, never the exception's type — and the real exceptions are fed to it, not only built ones.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class NativeProcessGoneTests
{
    [Theory]
    // The CI failure verbatim: ESRCH from a stat read that lost its process. The words are not the evidence, though...
    [InlineData(true, "No such process : '/proc/{0}/stat'", true)]
    [InlineData(true, "a message that says nothing about a process", true)]
    // ...so the same words about a pid that still exists prove nothing: unreadable is not gone.
    [InlineData(false, "No such process : '/proc/{0}/stat'", false)]
    public void A_plain_IOException_is_gone_exactly_when_the_kernel_says_the_pid_is_absent(bool pidIsAbsent, string message, bool expectedGone)
    {
        if (OperatingSystem.IsWindows()) return;

        var pid = pidIsAbsent ? ReapedPid() : Environment.ProcessId;

        NativeProcess.TreatsAsGone(pid, new IOException(string.Format(message, pid))).ShouldBe(expectedGone, $"an IOException saying '{message}' about pid {pid}, which the kernel reports {(pidIsAbsent ? "absent" : "alive")}");
    }

    [Theory]
    // The IOException family, whichever subclass the runtime picks for a vanished /proc entry.
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(FileNotFoundException), true)]
    [InlineData(typeof(DirectoryNotFoundException), true)]
    // What Identify's two runtime calls raise for a process that exits after its stat read: "not running" from GetProcessById, "information unavailable" from StartTime.
    [InlineData(typeof(ArgumentException), true)]
    [InlineData(typeof(Win32Exception), true)]
    // Nothing else is ever answered by the probe: a malformed stat or a denied read is a defect to surface, not a death.
    [InlineData(typeof(InvalidDataException), false)]
    [InlineData(typeof(UnauthorizedAccessException), false)]
    public void Only_a_failure_to_read_a_process_about_an_absent_pid_reads_as_a_process_that_is_gone(Type failureType, bool expectedGone)
    {
        if (OperatingSystem.IsWindows()) return;

        var failure = (Exception)Activator.CreateInstance(failureType)!;

        NativeProcess.TreatsAsGone(ReapedPid(), failure).ShouldBe(expectedGone, $"{failureType.Name} about a pid that names nothing");
    }

    [Theory]
    // The kernel decides, never the type: what reads as gone for an absent pid proves nothing about one that still exists.
    // (Win32Exception's own message says why: "It may have exited or may be privileged.")
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(Win32Exception))]
    public void A_failure_about_a_pid_that_still_exists_is_never_gone_whatever_its_type(Type failureType)
    {
        if (OperatingSystem.IsWindows()) return;

        var failure = (Exception)Activator.CreateInstance(failureType)!;

        NativeProcess.TreatsAsGone(Environment.ProcessId, failure).ShouldBeFalse($"{failureType.Name} about pid {Environment.ProcessId}, which is running this test");
    }

    [Fact]
    public void The_refusal_GetProcessById_raises_for_a_process_that_has_gone_is_one_TreatsAsGone_accepts()
    {
        if (OperatingSystem.IsWindows()) return;

        var pid = ReapedPid();
        var refusal = Should.Throw<ArgumentException>(() => Process.GetProcessById(pid));

        NativeProcess.TreatsAsGone(pid, refusal).ShouldBeTrue("the runtime's own refusal of a pid that names nothing, not a hand-built one: what Identify meets when the process exits after its stat read");
    }

    [Fact]
    public void The_refusal_StartTime_raises_for_a_process_that_exited_after_it_was_opened_is_one_TreatsAsGone_accepts()
    {
        // Only Linux's Identify reads StartTime (Darwin reads libproc), and this is Linux's runtime reading a vanished /proc entry.
        if (!OperatingSystem.IsLinux()) return;

        using var child = Process.Start(new ProcessStartInfo { FileName = "/bin/sh", ArgumentList = { "-c", "read line" }, UseShellExecute = false, RedirectStandardInput = true })!;
        using var opened = Process.GetProcessById(child.Id);
        child.StandardInput.Close();
        child.WaitForExit(30_000).ShouldBeTrue($"fixture check: pid {child.Id} must exit once its stdin closes — check `ps -p {child.Id}` by hand");

        var refusal = Should.Throw<Win32Exception>(() => opened.StartTime, $"fixture check: pid {child.Id} was opened alive and has since exited, so reading its start time must fail the way Identify's race does");

        NativeProcess.TreatsAsGone(child.Id, refusal).ShouldBeTrue("the runtime's own refusal of a process that exited after it was opened, not a hand-built one");
    }

    [Fact]
    public void Both_liveness_predicates_answer_gone_for_a_process_that_has_exited()
    {
        if (OperatingSystem.IsWindows()) return;

        var pid = ReapedPid();

        NativeProcess.IsRunning(pid).ShouldBeFalse("a reaped pid names no process, and gone is an answer rather than a fault");
        NativeProcess.IsAlive(NativeProcess.Current with { ProcessId = pid }).ShouldBeFalse("an identity whose process has exited is not alive");
        NativeProcess.IsRunning(Environment.ProcessId).ShouldBeTrue("the falsifier: this process is running, so 'gone for everyone' would pass the lines above");
    }

    [Fact]
    public void Identifying_a_process_that_has_exited_says_it_terminated()
    {
        if (OperatingSystem.IsWindows()) return;

        var refusal = Should.Throw<IOException>(() => NativeProcess.Identify(ReapedPid()));

        refusal.Message.ShouldBe("Cannot identify a terminated native process.", "the same refusal on every platform, not the raw path of a /proc entry that is no longer there");
    }

    /// <summary>A pid that names nothing: a trivial process started, allowed to exit and reaped — then checked absent by the runtime's own lookup, not by the code under test, so a reused pid fails the fixture instead of quietly measuring another process.</summary>
    private static int ReapedPid()
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = "/bin/sh", ArgumentList = { "-c", "exit 0" }, UseShellExecute = false })!;
        process.WaitForExit();

        Should.Throw<ArgumentException>(() => Process.GetProcessById(process.Id).Dispose(), $"fixture check: pid {process.Id} must be reaped, not reused, or the cases measure another process");

        return process.Id;
    }
}
