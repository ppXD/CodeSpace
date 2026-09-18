using System.Text.Json;
using Autofac;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// The services that <c>ScopedTransaction.OwnOrJoinAsync</c> converted, driven under BOTH caller shapes: with no
/// transaction open (the service owns and commits one) and inside a caller's transaction (it joins, and the caller
/// decides). Every direct-service test in this suite uses only the first shape, which is precisely why the second
/// went unnoticed for months — under the mediator these threw "the connection is already in a transaction and cannot
/// participate in another transaction", taking down the sweep, or the operator command, that reached them.
///
/// <para>Joining is only correct if it is honest in both directions, so the rolled-back case is a full member of
/// each theory: a joined write must land when the caller commits and must be GONE when the caller does not. A test
/// that only proved "no throw" would pass just as well against a helper that silently committed on its own.</para>
///
/// <para>Each case asserts a row THIS test seeded, read back in a fresh scope — never a sweep's global tally, which
/// this shared database cannot make deterministic.</para>
///
/// <para>The ambient shape of the two theories is SYNTHETIC: no production caller opens a transaction around the
/// budget ledger or the spool reaper today, because their commands carry <c>INonTransactionalCommand</c>. They pin
/// the helper's contract for the next caller, not a live path. The cancel cases below are the live one — that
/// command really is transactional, and really was broken.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ScopedTransactionFlowTests
{
    public enum Caller { NoTransaction, CommitsItsTransaction, RollsBackItsTransaction }

    private readonly PostgresFixture _fixture;

    public ScopedTransactionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(Caller.NoTransaction, BudgetReservationStates.Released)]
    [InlineData(Caller.CommitsItsTransaction, BudgetReservationStates.Released)]
    [InlineData(Caller.RollsBackItsTransaction, BudgetReservationStates.Reserved)]
    public async Task A_budget_release_lands_exactly_when_its_caller_does(Caller caller, string expectedState)
    {
        var teamId = (await SeedTeamAsync()).TeamId;
        var runId = await SeedWorkflowRunAsync(teamId, WorkflowRunStatus.Success);
        var scopeKey = $"ambient-{Guid.NewGuid():N}";
        var reservationId = await SeedReservationAsync(teamId, runId, scopeKey);

        await UnderCallerAsync(caller, scope => scope.Resolve<IBudgetLedger>().ReleaseAsync(runId, teamId, WorkflowEngine.MapBranchReservationKind, scopeKey, CancellationToken.None));

        using var verify = _fixture.BeginScope();
        var reservation = await verify.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().SingleAsync(r => r.Id == reservationId);

        reservation.State.ShouldBe(expectedState,
            customMessage: $"caller shape {caller} expected the release to end as {expectedState}. Released under a rolled-back " +
                           "caller means the ledger committed on its own instead of joining; Reserved under a committing one " +
                           "means its write never reached the caller's transaction.");
    }

    [Theory]
    [InlineData(Caller.NoTransaction, 1)]
    [InlineData(Caller.CommitsItsTransaction, 1)]
    [InlineData(Caller.RollsBackItsTransaction, 0)]
    public async Task A_spool_reap_retry_lands_exactly_when_its_caller_does(Caller caller, int expectedAttempts)
    {
        var teamId = (await SeedTeamAsync()).TeamId;
        var agentRunId = await SeedUnreapableTerminalRunAsync(teamId);

        await UnderCallerAsync(caller, scope => scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None));

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId);

        run.SpoolCleanupAttempts.ShouldBe(expectedAttempts,
            customMessage: $"caller shape {caller} expected {expectedAttempts} recorded cleanup attempt(s) for this run; the reaper " +
                           "defers a spool path outside its root and records the deferral inside the transaction it opened or joined.");
        run.RunnerHandleJson.ShouldNotBeNull("a deferred cleanup must keep its handle — the next sweep has nothing to retry without it");
    }

    /// <summary>
    /// The shape production actually has for the fourth converted site, and the reason it is a Fact rather than a
    /// theory case: <c>CancelRunCommand</c> is an ordinary <c>ICommand</c>, so <c>TransactionalBehavior</c> opens a
    /// transaction on the scoped context before <c>CancelRunAsync</c> ever runs. Opening a second one there threw,
    /// which means an operator's cancel could not work at all — and nothing noticed, because the only tests that send
    /// this command send it for a run id that does not exist and return before the transaction.
    /// </summary>
    [Fact]
    public async Task An_operator_cancel_completes_through_the_real_command_pipeline()
    {
        var team = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(team.TeamId, WorkflowRunStatus.Running);

        using (var scope = _fixture.BeginScopeAs(team.UserId, team.TeamId))
        {
            var outcome = await scope.Resolve<IMediator>().Send(new CancelRunCommand { RunId = runId });

            outcome!.Cancelled.ShouldBeTrue("the run was Running, so the operator's cancel must flip it rather than report an already-terminal no-op");
        }

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Cancelled,
            customMessage: "a fresh scope must see Cancelled: the flip and its ledger fact commit with the command's own transaction, " +
                           "which this service now joins instead of trying to open a second one beside it");
    }

    /// <summary>
    /// The consequence of joining, on the one path that now does it under a real command: the cancel's teardown must
    /// not run while the flip is still uncommitted. Tripping the run-cancellation registry there wakes an engine walk
    /// on this host, whose <c>WHERE Status = Running</c> UPDATE then blocks on the row this very transaction holds,
    /// while the kill-wave goes on writing <c>agent_run</c> from inside it — a cross-connection lock wait with a
    /// deadlock shape, with process kills in the middle of it.
    ///
    /// <para>Measured without racing anything: inside the transaction, the branch agent must still read Queued — a
    /// read that would see the wave's own uncommitted write if the teardown had run inline. It is only after the
    /// commit and the drain <c>TransactionalBehavior</c> performs next that the agent is Cancelled.</para>
    /// </summary>
    [Fact]
    public async Task An_ambient_cancel_defers_its_teardown_until_the_command_commits()
    {
        var team = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(team.TeamId, WorkflowRunStatus.Running);
        var agentRunId = await SeedQueuedBranchAgentAsync(team.TeamId, runId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var ambient = await db.Database.BeginTransactionAsync();

        var outcome = await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, team.TeamId, CancellationToken.None);

        outcome!.AgentRunsCancelled.ShouldBe(1, "the one live branch agent is what the kill-wave is handed");
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId)).Status.ShouldBe(AgentRunStatus.Queued,
            customMessage: "this read is inside the cancel's own transaction, so it would see the kill-wave's write if the teardown " +
                           "had run inline. Cancelled here means the wave ran while the run's terminal flip was still uncommitted.");

        await ambient.CommitAsync();
        await scope.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId)).Status
            .ShouldBe(AgentRunStatus.Cancelled, "the deferred teardown runs on the drain, so the branch agent is aborted after the commit — not never");
    }

    /// <summary>Runs <paramref name="work"/> in one scope, under the caller shape the case names, and resolves the outcome the way that caller would.</summary>
    private async Task UnderCallerAsync(Caller caller, Func<ILifetimeScope, Task> work)
    {
        using var scope = _fixture.BeginScope();

        if (caller == Caller.NoTransaction)
        {
            await work(scope);
            return;
        }

        await using var ambient = await scope.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync();
        await work(scope);

        if (caller == Caller.CommitsItsTransaction) await ambient.CommitAsync();
        else await ambient.RollbackAsync();
    }

    /// <summary>One unused map-branch reservation for this test's own (run, scope key) — the exact state <c>ReleaseAsync</c> returns to the cap.</summary>
    private async Task<Guid> SeedReservationAsync(Guid teamId, Guid workflowRunId, string scopeKey)
    {
        var reservationId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.BudgetReservation.Add(new BudgetReservation
        {
            Id = reservationId, TeamId = teamId, WorkflowRunId = workflowRunId,
            Kind = WorkflowEngine.MapBranchReservationKind, ScopeKey = scopeKey,
            State = BudgetReservationStates.Reserved, ReservedUsd = 1m, PriceVersion = "realized-v1",
        });

        await db.SaveChangesAsync();
        return reservationId;
    }

    /// <summary>
    /// A terminal run past its retention window whose handle points OUTSIDE the spool root. The containment guard
    /// refuses it, so the reaper takes its deferral path — which records an attempt through the transaction under
    /// test and touches no filesystem, so the only thing the caller's decision can change is the row.
    /// </summary>
    private async Task<Guid> SeedUnreapableTerminalRunAsync(Guid teamId)
    {
        var agentRunId = Guid.NewGuid();
        var handle = JsonSerializer.Serialize(new SandboxHandle
        {
            Kind = "local", ProcessId = 1, Deadline = DateTimeOffset.UtcNow,
            SpoolDirectory = $"/codespace-outside-any-spool-root-{Guid.NewGuid():N}", LaunchHost = LocalProcessRunner.CurrentHost,
        }, AgentJson.Options);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun { Id = agentRunId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, FenceEpoch = 1, RunnerHandleJson = handle });
        await db.SaveChangesAsync();

        // Terminal + long past retention in one UPDATE: the entity's audit stamps would otherwise fight the sweep's
        // own ordering, and this row must sort to the front of a batch this shared database also fills.
        await db.AgentRun.Where(r => r.Id == agentRunId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, AgentRunStatus.Succeeded).SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow.AddDays(-3650)));

        return agentRunId;
    }

    /// <summary>One Queued branch agent under the run — what the cancel's kill-wave is handed.</summary>
    private async Task<Guid> SeedQueuedBranchAgentAsync(Guid teamId, Guid workflowRunId)
    {
        var agentRunId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun { Id = agentRunId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Queued, WorkflowRunId = workflowRunId, NodeId = "agent", IterationKey = "" });
        await db.SaveChangesAsync();

        return agentRunId;
    }

    /// <summary>A parent workflow run (request + run) — WorkflowId null, since the FK is optional and no graph is needed here.</summary>
    private async Task<Guid> SeedWorkflowRunAsync(Guid teamId, WorkflowRunStatus status)
    {
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = DateTimeOffset.UtcNow, VerifiedAt = DateTimeOffset.UtcNow, NormalizedAt = DateTimeOffset.UtcNow,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual,
            Status = status, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>A team plus the Owner whose membership row the permission gate reads — the operator the cancel case signs in as.</summary>
    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"ambient-{userId:N}@test.local", Name = $"ambient-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"ambient-{teamId:N}", Name = "Ambient Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
