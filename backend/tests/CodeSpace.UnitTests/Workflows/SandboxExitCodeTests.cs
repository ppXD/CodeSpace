using CodeSpace.Core.Services.Agents.Sandbox;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="SandboxExitCode.Describe"/> — the 128+signal decode that turns an opaque "exited with code 137"
/// into a legible "killed by SIGKILL", so a runner-side resource-limit / OOM kill (the live-brain whole-loop
/// false-red, where the unconfined prlimit wrapper SIGKILL-ed the fake agent) is self-explanatory in AgentRun.Error
/// + the real-model verdict note instead of a number the reader has to decode by hand.
/// </summary>
public sealed class SandboxExitCodeTests
{
    [Theory]
    [InlineData(0, "0")]                  // normal success
    [InlineData(1, "1")]                  // ordinary app failure
    [InlineData(127, "127")]              // command-not-found — an app code, not a signal
    [InlineData(128, "128")]              // exactly 128 is NOT 128+signal (signum 0)
    [InlineData(193, "193")]              // 128+65 is past the last signal slot (signals run 1..64) → bare number
    public void A_non_signal_code_is_rendered_as_the_bare_number(int code, string expected)
    {
        SandboxExitCode.Describe(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData(137, "SIGKILL", 9)]       // OOM / RLIMIT_NPROC fork-kill / `kill -9`
    [InlineData(153, "SIGXFSZ", 25)]      // RLIMIT_FSIZE breach
    [InlineData(152, "SIGXCPU", 24)]      // RLIMIT_CPU breach
    [InlineData(143, "SIGTERM", 15)]      // graceful terminate
    [InlineData(139, "SIGSEGV", 11)]
    [InlineData(130, "SIGINT", 2)]
    public void A_128_plus_signal_code_names_the_signal_and_flags_a_likely_resource_kill(int code, string name, int signum)
    {
        var d = SandboxExitCode.Describe(code);

        d.ShouldStartWith(code.ToString());
        d.ShouldContain($"signal {name}/{signum}");
        d.ShouldContain("resource-limit kill", Case.Insensitive);   // the actionable hint — not an application error
    }

    [Fact]
    public void An_unnamed_signal_slot_still_decodes_as_a_signal_with_its_number()
    {
        // 128+40 = a realtime signal we don't name; it must still read as a signal, not a bare number.
        var d = SandboxExitCode.Describe(168);

        d.ShouldContain("terminated by signal 40");
        d.ShouldNotContain("SIGKILL");   // no NAME for slot 40 → number only, but still flagged as a signal
    }

    [Fact]
    public void The_vanished_sentinel_minus_one_reads_as_a_lost_process_not_a_bare_number()
    {
        // -1 is the durable runner's "process gone, no exit marker" sentinel (a whole-tree SIGKILL / host teardown).
        var d = SandboxExitCode.Describe(-1);

        d.ShouldStartWith("-1");
        d.ShouldContain("vanished", Case.Insensitive);
    }
}
