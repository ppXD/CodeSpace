using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 High fidelity (Rule 12) — the REAL <c>LocalProcessRunner</c> against REAL native launches and REAL OS
/// processes. Every case here is a terminate whose kill did or did not happen, asked to SAY WHICH.
///
/// <para>Before <see cref="SandboxTerminateResult"/> the terminate returned <c>void</c> and none of the withholding
/// paths threw, so a caller could not tell a killed tree from a kill the runner silently declined — which is how an
/// abandoned run could go on burning its injected credential with nothing anywhere recording it, and how the Linux
/// integration red could say no more than "should be True but was False".</para>
/// </summary>
public sealed partial class NativeLaunchRegistryTests
{
    [Fact]
    public async Task Terminate_that_kills_a_live_tree_reports_Killed()
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);

        var result = await fixture.Runner.TerminateAsync(handle, fixture.Token);

        result.Outcome.ShouldBe(SandboxTerminateOutcome.Killed, "the supervised tree was alive and the signal reached it");
        result.IsSettled.ShouldBeTrue();
        NativeProcess.IsAlive(fixture.ReadReceipt().Execution!).ShouldBeFalse();
    }

    [Fact]
    public async Task Terminate_of_an_already_dead_tree_reports_AlreadyGone_rather_than_a_kill_it_never_made()
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        await fixture.Runner.TerminateAsync(handle, fixture.Token);

        var second = await fixture.Runner.TerminateAsync(handle, fixture.Token);

        second.Outcome.ShouldBe(SandboxTerminateOutcome.AlreadyGone, "idempotence is a real outcome, not a kill — a caller that cannot tell them apart cannot audit either");
        second.IsSettled.ShouldBeTrue("the tree is provably not running, which is what the caller needed to know");
    }

    [Fact]
    public async Task Terminate_of_a_launch_another_host_minted_reports_SkippedNotLocal_and_names_both_hosts()
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var requestPath = Path.Combine(fixture.Directory, NativeLaunchProtocol.RequestFile);
        var original = await File.ReadAllTextAsync(requestPath, fixture.Token);
        var request = JsonSerializer.Deserialize<NativeLaunchRecord>(original, NativeLaunchProtocol.Json)!;

        try
        {
            // The launch must BIND (the handle agrees with the request in every other field) and still be refused
            // purely because the host that minted it is not this one — the cross-host skip, where the pid would name
            // an unrelated local process. Damaging the handle instead would land on the unresolvable path below.
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request with { Host = "another-worker" }, NativeLaunchProtocol.Json), fixture.Token);

            var result = await fixture.Runner.TerminateAsync(handle with { LaunchHost = "another-worker" }, fixture.Token);

            result.Outcome.ShouldBe(SandboxTerminateOutcome.SkippedNotLocal);
            result.IsSettled.ShouldBeFalse("nothing was killed, so nothing may be reported as settled");
            result.Detail.ShouldNotBeNull().ShouldContain("another-worker", customMessage: "the detail must name the host that owns the process, or the warning it lands in cannot be acted on");
            NativeProcess.IsAlive(fixture.ReadReceipt().Execution!).ShouldBeTrue("a foreign pid must not be signalled here");
        }
        finally { await File.WriteAllTextAsync(requestPath, original, CancellationToken.None); }
    }

    [Theory]
    // Each row damages a DIFFERENT gate of the handle→launch binding. They must not collapse into one message:
    // an unreadable receipt is transient (re-read and the kill may work), a spec-hash disagreement never will be.
    [InlineData("receipt-corrupt", "JsonException")]
    [InlineData("request-hash", "spec hash")]
    [InlineData("receipt-birth", "start commitment")]
    public async Task Terminate_that_cannot_bind_the_handle_reports_SkippedUnresolvableHandle_and_names_the_gate(string damage, string expectedDetail)
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var receiptPath = Path.Combine(fixture.Directory, NativeLaunchProtocol.ReceiptFile);
        var requestPath = Path.Combine(fixture.Directory, NativeLaunchProtocol.RequestFile);
        var originalReceipt = await File.ReadAllTextAsync(receiptPath, fixture.Token);
        var originalRequest = await File.ReadAllTextAsync(requestPath, fixture.Token);
        var receipt = fixture.ReadReceipt();

        try
        {
            await DamageAsync(damage, receiptPath, requestPath, receipt, originalRequest, fixture.Token);

            var result = await fixture.Runner.TerminateAsync(handle, fixture.Token);

            result.Outcome.ShouldBe(SandboxTerminateOutcome.SkippedUnresolvableHandle);
            result.Detail.ShouldNotBeNull().ShouldContain(expectedDetail, Case.Insensitive,
                $"'{damage}' must be distinguishable from the other binding refusals — a single 'the handle did not resolve' is not something an operator can act on");
            NativeProcess.IsAlive(receipt.Execution!).ShouldBeTrue("an unbound handle must withhold the signal");
        }
        finally
        {
            await File.WriteAllTextAsync(receiptPath, originalReceipt, CancellationToken.None);
            await File.WriteAllTextAsync(requestPath, originalRequest, CancellationToken.None);
        }
    }

    private static async Task DamageAsync(string damage, string receiptPath, string requestPath, NativeLaunchReceipt receipt, string originalRequest, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<NativeLaunchRecord>(originalRequest, NativeLaunchProtocol.Json)!;

        switch (damage)
        {
            case "receipt-corrupt": await File.WriteAllTextAsync(receiptPath, "{", cancellationToken); break;
            case "request-hash": await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request with { SpecHash = "other-spec" }, NativeLaunchProtocol.Json), cancellationToken); break;
            case "receipt-birth": await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt with { Execution = receipt.Execution! with { StartKey = "other-birth" } }, NativeLaunchProtocol.Json), cancellationToken); break;
            default: throw new ArgumentOutOfRangeException(nameof(damage), damage, "unknown binding damage");
        }
    }

    [Fact]
    public async Task Terminate_whose_tree_outlives_the_reap_wait_reports_TimedOutWaitingReap_instead_of_a_kill()
    {
        // POSIX-only (Rule 12.1): the durable supervisor is /bin/sh and the birth key this case rewrites comes from
        // /proc or libproc, so there is no Windows implementation to exercise. This project is on xunit 2.9.2, which
        // has no runtime skip (Xunit.SkippableFact is referenced by the INTEGRATION project only), so the guard is an
        // early return naming its reason — the same shape every POSIX-only case in this suite uses.
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var receiptPath = Path.Combine(fixture.Directory, NativeLaunchProtocol.ReceiptFile);
        var executionPath = Path.Combine(fixture.Directory, NativeLaunchProtocol.ExecutionFile);
        var originalReceipt = await File.ReadAllTextAsync(receiptPath, fixture.Token);
        var originalExecution = await File.ReadAllTextAsync(executionPath, fixture.Token);
        var receipt = fixture.ReadReceipt();

        try
        {
            // The one process on this host that is alive, correctly bound, and that KillSession refuses to signal:
            // the caller itself (it guards `ProcessId == Environment.ProcessId`). So the signal is withheld by the
            // kernel-facing layer, the tree stays alive, and the bounded reap wait is the only thing that ends the
            // call — the exact shape of a kill that was issued and never took effect.
            var self = NativeProcess.Identify(Environment.ProcessId);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt with { Execution = self }, NativeLaunchProtocol.Json), fixture.Token);
            await File.WriteAllTextAsync(executionPath, JsonSerializer.Serialize(self, NativeLaunchProtocol.Json), fixture.Token);
            var projected = handle with { ProcessId = self.ProcessId, ProcessStartTimeUtc = new DateTimeOffset(self.StartTimeUtcTicks, TimeSpan.Zero), NativeLaunch = null };

            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await fixture.Runner.TerminateAsync(projected, bound.Token);

            result.Outcome.ShouldBe(SandboxTerminateOutcome.TimedOutWaitingReap,
                "the tree was still alive when the bounded wait expired; reporting anything settled would be a claim nothing observed");
            result.IsSettled.ShouldBeFalse();
            result.Detail.ShouldNotBeNull().ShouldContain(self.ProcessId.ToString(), customMessage: "the detail must name the pid that outlived the wait");
            NativeProcess.IsAlive(self).ShouldBeTrue("the test process is still alive — a terminate must never reach it");
        }
        finally
        {
            await File.WriteAllTextAsync(receiptPath, originalReceipt, CancellationToken.None);
            await File.WriteAllTextAsync(executionPath, originalExecution, CancellationToken.None);
            await fixture.Runner.TerminateAsync(handle, CancellationToken.None);
        }
    }

    [Theory]
    // The legacy (pre-native-handle) verdict, every branch. Two of these five cannot be staged against a real OS —
    // SIGKILL cannot be survived, and a /proc this process is refused cannot be arranged — which is exactly why the
    // verdict is separated from the observations that feed it. Collapse any two rows and this goes red.
    [InlineData(true, SandboxRunState.Running, SandboxRunState.Gone, SandboxTerminateOutcome.Killed)]
    [InlineData(true, SandboxRunState.Gone, SandboxRunState.Gone, SandboxTerminateOutcome.AlreadyGone)]
    [InlineData(true, SandboxRunState.Running, SandboxRunState.Running, SandboxTerminateOutcome.TimedOutWaitingReap)]
    [InlineData(true, SandboxRunState.Running, SandboxRunState.Indeterminate, SandboxTerminateOutcome.SkippedIndeterminate)]
    // Not-local outranks every observation: the pid is not ours to read, so neither answer about it means anything.
    [InlineData(false, SandboxRunState.Running, SandboxRunState.Gone, SandboxTerminateOutcome.SkippedNotLocal)]
    public void The_legacy_verdict_separates_a_kill_from_a_corpse_from_a_withheld_signal(bool local, SandboxRunState before, SandboxRunState after, SandboxTerminateOutcome expected)
    {
        var handle = new SandboxHandle { Kind = LocalProcessRunner.LocalKind, ProcessId = 4242, SpoolDirectory = "/spool/legacy", Deadline = DateTimeOffset.UtcNow.AddMinutes(5), LaunchHost = local ? LocalProcessRunner.CurrentHost : "another-worker" };

        var result = LocalProcessRunner.LegacyTerminationOutcome(handle, before, after);

        result.Outcome.ShouldBe(expected);
        result.IsSettled.ShouldBe(expected is SandboxTerminateOutcome.Killed or SandboxTerminateOutcome.AlreadyGone, "only an OBSERVED death may be reported as settled");
        if (!result.IsSettled) result.Detail.ShouldNotBeNull().ShouldContain("4242", customMessage: "a withheld legacy kill must name the pid nobody can account for");
    }

    [Theory]
    // The native path's unreadable-liveness branch, whose two phases are different facts: before the signal nothing was
    // attempted, after it something was and its effect is unknown. Same outcome, and the detail must not blur them.
    [InlineData("before any signal was issued")]
    [InlineData("after the kill signal was issued")]
    public void An_unobservable_liveness_is_reported_as_such_with_the_phase_it_happened_in(string phase)
    {
        var identity = new NativeProcessIdentity(4242, 1, "boot", "linux:1");

        var result = LocalProcessRunner.IndeterminateTerminateResult(identity, phase);

        result.Outcome.ShouldBe(SandboxTerminateOutcome.SkippedIndeterminate);
        result.IsSettled.ShouldBeFalse();
        result.Detail.ShouldNotBeNull().ShouldContain(phase, customMessage: "a kill that was issued and one that never was must not read identically");
        result.Detail.ShouldContain("4242");
    }
}
