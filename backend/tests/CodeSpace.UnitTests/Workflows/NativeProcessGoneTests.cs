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
    // Nothing else is ever answered by the probe: a malformed stat or a denied read is a defect to surface, not a death.
    [InlineData(typeof(InvalidDataException), false)]
    [InlineData(typeof(UnauthorizedAccessException), false)]
    public void Only_an_IO_failure_about_an_absent_pid_reads_as_a_process_that_is_gone(Type failureType, bool expectedGone)
    {
        if (OperatingSystem.IsWindows()) return;

        var failure = (Exception)Activator.CreateInstance(failureType)!;

        NativeProcess.TreatsAsGone(ReapedPid(), failure).ShouldBe(expectedGone, $"{failureType.Name} about a pid that names nothing");
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
