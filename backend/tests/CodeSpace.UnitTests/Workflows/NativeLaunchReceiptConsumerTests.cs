using System.Text.Json;
using CodeSpace.Core.Services.Agents;
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
    [Theory]
    [InlineData("receipt-corrupt")]
    [InlineData("receipt-birth")]
    [InlineData("request-hash")]
    [InlineData("request-boot")]
    [InlineData("registry-missing")]
    public async Task Native_consumer_cannot_downgrade_unavailable_or_conflicting_receipt_to_PID_or_exit_marker(string damage)
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var directory = NativeLaunchFiles.DirectoryFor(handle.SpoolDirectory);
        var receiptPath = Path.Combine(directory, NativeLaunchProtocol.ReceiptFile);
        var requestPath = Path.Combine(directory, NativeLaunchProtocol.RequestFile);
        var originalReceipt = await File.ReadAllTextAsync(receiptPath);
        var originalRequest = await File.ReadAllTextAsync(requestPath);
        var receipt = fixture.ReadReceipt();
        var request = JsonSerializer.Deserialize<NativeLaunchRecord>(originalRequest, NativeLaunchProtocol.Json)!;
        try
        {
            switch (damage)
            {
                case "receipt-corrupt": await File.WriteAllTextAsync(receiptPath, "{"); break;
                case "receipt-birth": await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt with { Execution = receipt.Execution! with { StartKey = "other-birth" } }, NativeLaunchProtocol.Json)); break;
                case "request-hash": await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request with { SpecHash = "other-spec" }, NativeLaunchProtocol.Json)); break;
                case "request-boot": await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request with { BootId = "other-boot" }, NativeLaunchProtocol.Json)); break;
                case "registry-missing": Directory.Move(directory, directory + "-retained-for-cleanup"); break;
            }
            await File.WriteAllTextAsync(Path.Combine(handle.SpoolDirectory, "exit"), "0");
            (await fixture.Runner.ProbeAsync(handle, fixture.Token)).State.ShouldBe(SandboxRunState.Indeterminate, "neither a PID nor an unbound exit marker substitutes for the required native receipt");
            await fixture.Runner.TerminateAsync(handle, fixture.Token);
            NativeProcess.IsAlive(receipt.Execution!).ShouldBeTrue("invalid identity must also withhold the signal");
        }
        finally
        {
            if (damage == "registry-missing") Directory.Move(directory + "-retained-for-cleanup", directory);
            await File.WriteAllTextAsync(receiptPath, originalReceipt);
            await File.WriteAllTextAsync(requestPath, originalRequest);
            File.Delete(Path.Combine(handle.SpoolDirectory, "exit"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bound_native_handle_and_A_era_projection_observe_and_terminate_the_same_execution(bool aEraProjection)
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        handle.NativeLaunch.ShouldNotBeNull();
        if (aEraProjection) handle = handle with { NativeLaunch = null };
        var serialized = JsonSerializer.Serialize(handle, AgentJson.Options);
        if (aEraProjection) serialized.ShouldNotContain("nativeLaunch", customMessage: "legacy JSON must not acquire a null field that changes existing canonical hashes");
        handle = JsonSerializer.Deserialize<SandboxHandle>(serialized, AgentJson.Options)!;
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        (await fixture.Runner.ProbeAsync(handle, fixture.Token)).State.ShouldBe(SandboxRunState.Running);
        await fixture.Runner.TerminateAsync(handle, fixture.Token);
        NativeProcess.IsAlive(fixture.ReadReceipt().Execution!).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid\0path")]
    public async Task Native_consumer_reports_malformed_locator_as_unknown_without_signalling(string? locator)
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var malformed = handle with { SpoolDirectory = locator! };
        (await fixture.Runner.ProbeAsync(malformed, fixture.Token)).State.ShouldBe(SandboxRunState.Indeterminate);
        await fixture.Runner.TerminateAsync(malformed, fixture.Token);
        NativeProcess.IsAlive(fixture.ReadReceipt().Execution!).ShouldBeTrue();
    }

}
