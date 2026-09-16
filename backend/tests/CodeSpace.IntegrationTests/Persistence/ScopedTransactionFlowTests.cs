using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Learning;
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
    [InlineData(Caller.NoTransaction, true)]
    [InlineData(Caller.CommitsItsTransaction, true)]
    [InlineData(Caller.RollsBackItsTransaction, false)]
    public async Task A_lesson_qualification_sweep_lands_exactly_when_its_caller_does(Caller caller, bool advanced)
    {
        var teamId = (await SeedTeamAsync()).TeamId;
        var lessonId = await SeedLessonAsync(teamId);

        await UnderCallerAsync(caller, scope => scope.Resolve<ILessonQualifier>().QualifyAsync(CancellationToken.None));

        using var verify = _fixture.BeginScope();
        var lesson = await verify.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().SingleAsync(l => l.Id == lessonId);

        (lesson.QualificationCheckedAt > SweepCursorFloor).ShouldBe(advanced,
            customMessage: $"caller shape {caller} expected the fairness cursor to have {(advanced ? "advanced" : "stayed at its seeded floor")}; " +
                           $"it reads {lesson.QualificationCheckedAt:O}. The sweep's own transaction must follow the caller's, not its own mind.");
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

    /// <summary>The seeded fairness cursor: old enough to be swept first out of whatever else this shared database holds, and a value the sweep can only move forwards.</summary>
    private static readonly DateTimeOffset SweepCursorFloor = DateTimeOffset.UnixEpoch;

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

    /// <summary>A live, unqualified lesson whose fairness cursor sits at the floor, so the bounded sweep reaches it however many other lessons this database holds.</summary>
    private async Task<Guid> SeedLessonAsync(Guid teamId)
    {
        var lessonId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Lesson.Add(new Lesson
        {
            Id = lessonId, TeamId = teamId, Mode = RunModeKeys.PlanMap, FailureClass = "ambient-transaction",
            WhatFailed = "a service opened its own transaction inside its caller's", Why = "npgsql refuses a nested transaction",
            HowToApply = "join the ambient transaction", SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test-model",
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            QualificationCheckedAt = SweepCursorFloor,
        });

        await db.SaveChangesAsync();
        return lessonId;
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
