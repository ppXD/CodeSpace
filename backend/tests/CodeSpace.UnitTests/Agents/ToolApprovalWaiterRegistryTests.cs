using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit (high fidelity — real production registry, pure in-memory, no external resource).
/// Pins the in-process rendezvous between a blocked approval handler and the resolver: a registered waiter's
/// Completion stays pending until signaled, TrySignal completes it with the outcome (and reports a live waiter),
/// signaling an absent id is a harmless false, Remove is idempotent, and a double-Register overwrites cleanly.
/// </summary>
[Trait("Category", "Unit")]
public class ToolApprovalWaiterRegistryTests
{
    [Fact]
    public void Register_returns_a_pending_completion()
    {
        var registry = new ToolApprovalWaiterRegistry();

        var waiter = registry.Register(Guid.NewGuid());

        waiter.Completion.IsCompleted.ShouldBeFalse("a freshly-registered waiter blocks until a decision is signaled");
    }

    [Theory]
    [InlineData(ToolApprovalOutcome.Approved)]
    [InlineData(ToolApprovalOutcome.Rejected)]
    [InlineData(ToolApprovalOutcome.Expired)]
    public async Task TrySignal_completes_the_waiter_with_the_outcome(ToolApprovalOutcome outcome)
    {
        var registry = new ToolApprovalWaiterRegistry();
        var ledgerId = Guid.NewGuid();
        var waiter = registry.Register(ledgerId);

        var signaled = registry.TrySignal(ledgerId, outcome);

        signaled.ShouldBeTrue("a live waiter was present");
        (await waiter.Completion).ShouldBe(outcome, "the waiter wakes with exactly the signaled verdict");
    }

    [Fact]
    public void TrySignal_on_an_absent_id_returns_false()
    {
        var registry = new ToolApprovalWaiterRegistry();

        registry.TrySignal(Guid.NewGuid(), ToolApprovalOutcome.Approved)
            .ShouldBeFalse("no live waiter — the common D0 case where nothing is blocked, harmless");
    }

    [Fact]
    public void Remove_is_idempotent()
    {
        var registry = new ToolApprovalWaiterRegistry();
        var ledgerId = Guid.NewGuid();
        registry.Register(ledgerId);

        registry.Remove(ledgerId);
        Should.NotThrow(() => registry.Remove(ledgerId));

        registry.TrySignal(ledgerId, ToolApprovalOutcome.Approved).ShouldBeFalse("the waiter is gone after Remove");
    }

    [Fact]
    public async Task Double_Register_overwrites_cleanly_and_only_the_latest_waiter_is_signaled()
    {
        var registry = new ToolApprovalWaiterRegistry();
        var ledgerId = Guid.NewGuid();

        var first = registry.Register(ledgerId);
        var second = registry.Register(ledgerId);

        registry.TrySignal(ledgerId, ToolApprovalOutcome.Approved).ShouldBeTrue();

        (await second.Completion).ShouldBe(ToolApprovalOutcome.Approved, "the latest registration is the one that wakes");
        first.Completion.IsCompleted.ShouldBeFalse("the overwritten waiter is orphaned, not signaled");
    }
}
