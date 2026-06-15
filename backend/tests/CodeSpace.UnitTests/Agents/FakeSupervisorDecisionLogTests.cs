using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the SHARED <see cref="FakeSupervisorDecisionLog"/> enforces the SAME transition legality as the real
/// <c>SupervisorDecisionLog</c> — because both DELEGATE to <see cref="SupervisorDecisionStateMachine"/> rather
/// than re-implementing the rules (Rule 12.5: the mirror is ELIMINATED, so there is nothing to drift). These
/// tests pin that the fake REJECTS exactly the illegal hops the real ledger's <c>RecordTerminalAsync</c> rejects
/// (a <see cref="SupervisorDecisionTransitionException"/>), so a test relying on this fake can never pass on an
/// illegal lifecycle the real ledger would have thrown on. The legality matrix itself is pinned exhaustively in
/// <c>SupervisorDecisionStateMachineTests</c>; here we prove the fake honours it.
/// </summary>
[Trait("Category", "Unit")]
public class FakeSupervisorDecisionLogTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();

    [Fact]
    public async Task A_terminal_record_without_the_Running_claim_hop_is_rejected()
    {
        // Pending → Succeeded with NO intervening Pending → Running claim is illegal (the state machine makes a
        // terminal reachable ONLY from Running — the must-fix-#2 mandatory claim hop). The fake throws, exactly
        // as the real RecordTerminalAsync would, instead of silently flipping the row (the old fake's drift).
        var fake = new FakeSupervisorDecisionLog();
        var claim = await fake.TryClaimAsync(_runId, _teamId, SupervisorDecisionKinds.Plan, "k", "h", "{}", fenceEpoch: 0, CancellationToken.None);

        var ex = await Should.ThrowAsync<SupervisorDecisionTransitionException>(
            () => fake.RecordTerminalAsync(claim.DecisionId, _teamId, SupervisorDecisionStatus.Succeeded, "{}", error: null, CancellationToken.None));

        ex.Message.ShouldContain("Illegal SupervisorDecision transition Pending → Succeeded");
    }

    [Fact]
    public async Task A_double_terminal_is_rejected()
    {
        // Claim → Running → Succeeded is legal; a SECOND terminal record (Succeeded → Failed) is illegal — a
        // terminal never transitions out. The fake rejects it like the real ledger's status-guarded CAS.
        var fake = new FakeSupervisorDecisionLog();
        var claim = await fake.TryClaimAsync(_runId, _teamId, SupervisorDecisionKinds.Plan, "k", "h", "{}", fenceEpoch: 0, CancellationToken.None);

        (await fake.TryBeginExecutionAsync(claim.DecisionId, _teamId, CancellationToken.None)).ShouldBeTrue("the claim hop wins Pending → Running");
        await fake.RecordTerminalAsync(claim.DecisionId, _teamId, SupervisorDecisionStatus.Succeeded, "{}", error: null, CancellationToken.None);

        var ex = await Should.ThrowAsync<SupervisorDecisionTransitionException>(
            () => fake.RecordTerminalAsync(claim.DecisionId, _teamId, SupervisorDecisionStatus.Failed, "{}", error: "x", CancellationToken.None));

        ex.Message.ShouldContain("Illegal SupervisorDecision transition Succeeded → Failed");
    }

    [Fact]
    public async Task A_non_terminal_status_passed_to_RecordTerminal_is_rejected()
    {
        // RecordTerminalAsync only accepts a TERMINAL target (mirrors the real ledger's IsTerminal guard); a
        // Running target throws before any legality check.
        var fake = new FakeSupervisorDecisionLog();
        var claim = await fake.TryClaimAsync(_runId, _teamId, SupervisorDecisionKinds.Plan, "k", "h", "{}", fenceEpoch: 0, CancellationToken.None);

        var ex = await Should.ThrowAsync<SupervisorDecisionTransitionException>(
            () => fake.RecordTerminalAsync(claim.DecisionId, _teamId, SupervisorDecisionStatus.Running, "{}", error: null, CancellationToken.None));

        ex.Message.ShouldContain("terminal status must be terminal");
    }

    [Fact]
    public async Task A_second_begin_execution_loses_the_single_winner_claim()
    {
        // The Pending → Running claim is single-winner: the first call wins (true), a second sees the row already
        // Running (no longer a legal Pending/AwaitingApproval source) → false. This is the replay path the loser
        // takes, modelled exactly as the real CAS.
        var fake = new FakeSupervisorDecisionLog();
        var claim = await fake.TryClaimAsync(_runId, _teamId, SupervisorDecisionKinds.Plan, "k", "h", "{}", fenceEpoch: 0, CancellationToken.None);

        (await fake.TryBeginExecutionAsync(claim.DecisionId, _teamId, CancellationToken.None)).ShouldBeTrue("first claimer wins");
        (await fake.TryBeginExecutionAsync(claim.DecisionId, _teamId, CancellationToken.None)).ShouldBeFalse("a second begin loses — the row is already Running");
    }
}
