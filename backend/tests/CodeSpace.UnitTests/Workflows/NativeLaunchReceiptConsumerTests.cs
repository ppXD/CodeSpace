using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public sealed partial class NativeLaunchRegistryTests
{
    [Theory]
    [InlineData("probe")]
    [InlineData("terminate")]
    public async Task Receipt_backed_consumers_reject_a_handle_mixed_with_another_live_process(string operation)
    {
        await using var original = new Fixture();
        await using var unrelated = new Fixture();
        var originalHandle = await original.LaunchAsync();
        var unrelatedHandle = await unrelated.LaunchAsync();
        await original.WaitAsync(() => original.StartCount == 1);
        await unrelated.WaitAsync(() => unrelated.StartCount == 1);
        var originalIdentity = original.ReadReceipt().Execution!;
        var unrelatedIdentity = unrelated.ReadReceipt().Execution!;
        originalIdentity.ProcessId.ShouldNotBe(unrelatedIdentity.ProcessId);
        NativeProcess.IsAlive(originalIdentity).ShouldBeTrue();
        NativeProcess.IsAlive(unrelatedIdentity).ShouldBeTrue();

        // Both processes and receipts are real. This corrupted projection still points at the ORIGINAL
        // retained launch receipt; the other PID's valid wall time does not bind it to that launch.
        var mixed = originalHandle with { ProcessId = unrelatedHandle.ProcessId, ProcessStartTimeUtc = unrelatedHandle.ProcessStartTimeUtc };
        if (operation == "probe")
        {
            var probe = await original.Runner.ProbeAsync(mixed, original.Token);
            probe.State.ShouldBe(SandboxRunState.Indeterminate, "an unrelated live PID must not satisfy the original launch's liveness lookup");
        }
        else
        {
            await original.Runner.TerminateAsync(mixed, original.Token);
            NativeProcess.IsAlive(unrelatedIdentity).ShouldBeTrue("an original receipt plus another execution's PID/wall-time projection must not authorize a signal to that other process");
        }
        NativeProcess.IsAlive(originalIdentity).ShouldBeTrue();
    }
}
