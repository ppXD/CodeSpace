using System.Data.Common;
using System.Diagnostics;
using Autofac;
using Microsoft.EntityFrameworkCore.Diagnostics;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Messages.Enums;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunExplicitOwnershipFlowTests
{
    private readonly PostgresFixture _fixture;
    public AgentRunExplicitOwnershipFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData("heartbeat")]
    [InlineData("handle")]
    [InlineData("posture")]
    [InlineData("events")]
    public async Task A_run_id_only_old_worker_cannot_write_after_a_reconciler_reclaim(string write)
    {
        var runId = await CreateLegacyRunningAsync();
        using (var reclaim = _fixture.BeginScope())
        {
            await reclaim.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            (await reclaim.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();
        }
        using var stale = _fixture.BeginScope();
        var service = stale.Resolve<IAgentRunService>();
        var prior = await service.GetAsync(runId, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => write switch
        {
            "heartbeat" => service.HeartbeatAsync(runId, CancellationToken.None),
            "handle" => service.SetRunnerHandleAsync(runId, "{\"stale\":true}", CancellationToken.None),
            "posture" => service.SetSandboxConfinementAsync(runId, "{\"stale\":true}", CancellationToken.None),
            _ => service.AppendEventsAsync(runId, [new() { Kind = AgentEventKind.AssistantMessage, Text = "stale-worker-event" }], CancellationToken.None),
        });
        failure.ShouldNotBeNull("a run id and a formerly held epoch do not authorize the current observer's writes");
        var after = await service.GetAsync(runId, CancellationToken.None);
        after.HeartbeatAt.ShouldBe(prior.HeartbeatAt);
        after.RunnerHandleJson.ShouldBe(prior.RunnerHandleJson);
        after.SandboxConfinementJson.ShouldBe(prior.SandboxConfinementJson);
        (await stale.Resolve<CodeSpaceDbContext>().AgentRunEvent.CountAsync(e => e.AgentRunId == runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_claims_and_duplicate_activation_have_one_owner_and_frozen_reservation_identity()
    {
        var runId = await CreateQueuedAsync();
        async Task<AgentRunOwnerToken?> ClaimAsync()
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IAgentRunService>().ClaimOwnershipAsync(runId, CancellationToken.None);
        }
        var claims = await Task.WhenAll(ClaimAsync(), ClaimAsync());
        var first = claims.Single(c => c != null)!;
        claims.Count(c => c != null).ShouldBe(1);
        first.OwnerId.ShouldNotBe(Guid.Empty);
        first.Epoch.ShouldBe(1);
        using var reservationScope = _fixture.BeginScope();
        var runs = reservationScope.Resolve<IAgentRunService>();
        await ExpireAsync(reservationScope, runId);
        var reservation = (await runs.ReserveReattachAsync(runId, CancellationToken.None))!;
        reservation.ShouldNotBeNull();
        reservation.Epoch.ShouldBe(first.Epoch + 1);
        (await runs.ActivateReattachAsync(reservation with { ReservationId = Guid.NewGuid() }, CancellationToken.None)).ShouldBeNull();
        async Task<AgentRunOwnerToken?> ActivateAsync()
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IAgentRunService>().ActivateReattachAsync(reservation, CancellationToken.None);
        }
        var activations = await Task.WhenAll(ActivateAsync(), ActivateAsync());
        var second = activations.Single(c => c != null)!;
        activations.Count(c => c != null).ShouldBe(1);
        second.OwnerId.ShouldNotBe(first.OwnerId);
        second.Epoch.ShouldBe(reservation.Epoch);
        (await runs.ActivateReattachAsync(reservation, CancellationToken.None)).ShouldBeNull();
        await Should.ThrowAsync<AgentRunOwnershipLostException>(() => runs.AssertOwnershipAsync(first, CancellationToken.None));
        await runs.AssertOwnershipAsync(second, CancellationToken.None);
    }

    [Fact]
    public async Task An_owned_new_runner_handle_clears_retry_state_from_the_previous_cleanup_obligation()
    {
        var runId = await CreateQueuedAsync();
        using var scope = _fixture.BeginScope();
        var owner = (await scope.Resolve<IAgentRunService>().ClaimOwnershipAsync(runId, CancellationToken.None))!;
        await scope.Resolve<CodeSpaceDbContext>().AgentRun.Where(row => row.Id == runId).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.SpoolCleanupAttempts, 7)
            .SetProperty(row => row.SpoolCleanupLastAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))
            .SetProperty(row => row.SpoolCleanupNextAttemptAt, DateTimeOffset.UtcNow.AddHours(2))
            .SetProperty(row => row.SpoolCleanupLastErrorCode, "filesystem-io"));

        await scope.Resolve<IAgentRunService>().SetRunnerHandleAsync(owner, "{\"kind\":\"local\"}", CancellationToken.None);

        var actual = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(row => row.Id == runId);
        actual.SpoolCleanupAttempts.ShouldBe(0);
        actual.SpoolCleanupLastAttemptAt.ShouldBeNull();
        actual.SpoolCleanupNextAttemptAt.ShouldBeNull();
        actual.SpoolCleanupLastErrorCode.ShouldBeNull();
    }

    [Theory]
    [InlineData("heartbeat")]
    [InlineData("handle")]
    [InlineData("posture")]
    [InlineData("events")]
    [InlineData("terminal")]
    public async Task Revived_owner_and_wrong_uuid_at_current_epoch_cannot_mutate_current_worker(string write)
    {
        var runId = await CreateQueuedAsync();
        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IAgentRunService>();
        var old = (await service.ClaimOwnershipAsync(runId, CancellationToken.None))!;
        await ExpireAsync(scope, runId);
        var reservation = (await service.ReserveReattachAsync(runId, CancellationToken.None))!;
        var current = (await service.ActivateReattachAsync(reservation, CancellationToken.None))!;
        var before = await service.GetAsync(runId, CancellationToken.None);
        foreach (var stale in new[] { old, current with { OwnerId = old.OwnerId } })
        {
            await Should.ThrowAsync<AgentRunOwnershipLostException>(() => write switch
            {
                "heartbeat" => service.HeartbeatAsync(stale, CancellationToken.None),
                "handle" => service.SetRunnerHandleAsync(stale, "{\"stale\":true}", CancellationToken.None),
                "posture" => service.SetSandboxConfinementAsync(stale, "{\"stale\":true}", CancellationToken.None),
                "terminal" => service.CompleteAsync(stale, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None),
                _ => service.AppendEventsAsync(stale, [new() { Kind = AgentEventKind.AssistantMessage, Text = "stale" }], CancellationToken.None),
            });
        }
        var after = await service.GetAsync(runId, CancellationToken.None);
        after.Status.ShouldBe(AgentRunStatus.Running);
        after.HeartbeatAt.ShouldBe(before.HeartbeatAt);
        after.RunnerHandleJson.ShouldBe(before.RunnerHandleJson);
        after.SandboxConfinementJson.ShouldBe(before.SandboxConfinementJson);
        (await scope.Resolve<CodeSpaceDbContext>().AgentRunEvent.CountAsync(e => e.AgentRunId == runId)).ShouldBe(0);
        await service.AppendEventsAsync(current, [new() { Kind = AgentEventKind.AssistantMessage, Text = "current" }], CancellationToken.None);
        await service.AppendSystemEventAsync(runId, new() { Kind = AgentEventKind.Warning, Text = "system audit" }, CancellationToken.None);
        var events = await scope.Resolve<CodeSpaceDbContext>().AgentRunEvent.AsNoTracking().Where(e => e.AgentRunId == runId).OrderBy(e => e.Sequence).ToListAsync();
        events.Count.ShouldBe(2);
        events[0].WriterKind.ShouldBe("worker");
        events[0].WriterOwnerId.ShouldBe(current.OwnerId);
        events[0].WriterEpoch.ShouldBe(current.Epoch);
        events[1].WriterKind.ShouldBe("system");
        events[1].WriterOwnerId.ShouldBeNull();
        events[1].WriterEpoch.ShouldBeNull();
    }

    [Fact]
    public async Task Expired_owner_cannot_renew_and_expired_reservation_cannot_activate()
    {
        var runId = await CreateQueuedAsync();
        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IAgentRunService>();
        var owner = (await service.ClaimOwnershipAsync(runId, CancellationToken.None))!;
        await ExpireAsync(scope, runId);
        await Should.ThrowAsync<AgentRunOwnershipLostException>(() => service.HeartbeatAsync(owner, CancellationToken.None));
        (await service.ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeFalse("the legacy reservation API cannot mutate a modern run even after expiry");
        var reservation = (await service.ReserveReattachAsync(runId, CancellationToken.None))!;
        await ExpireAsync(scope, runId);
        (await service.ActivateReattachAsync(reservation, CancellationToken.None)).ShouldBeNull();
        (await service.ReserveReattachAsync(runId, CancellationToken.None))!.ReservationId.ShouldNotBe(reservation.ReservationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_commit_ack_recovers_only_the_same_minted_claim_or_activation_even_if_caller_cancelled(bool activate)
    {
        var runId = await CreateQueuedAsync();
        AgentRunReattachReservation? reservation = null;
        using (var setup = _fixture.BeginScope())
        {
            if (activate)
            {
                await setup.Resolve<IAgentRunService>().ClaimOwnershipAsync(runId, CancellationToken.None);
                await ExpireAsync(setup, runId);
                reservation = (await setup.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            }
        }
        using var caller = new CancellationTokenSource();
        var fault = new OwnershipCommitFault { AfterCommit = () => { caller.Cancel(); return Task.CompletedTask; } };
        using var scope = FaultScope(fault);
        var runs = scope.Resolve<IAgentRunService>();
        var owner = activate ? await runs.ActivateReattachAsync(reservation!, caller.Token) : await runs.ClaimOwnershipAsync(runId, caller.Token);
        fault.Fired.ShouldBeTrue();
        caller.IsCancellationRequested.ShouldBeTrue();
        owner.ShouldNotBeNull();
        using var verify = _fixture.BeginScope();
        var persisted = await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        persisted.OwnerId.ShouldBe(owner.OwnerId);
        persisted.FenceEpoch.ShouldBe(owner.Epoch);
        await verify.Resolve<IAgentRunService>().AssertOwnershipAsync(owner, CancellationToken.None);
        if (activate) (await verify.Resolve<IAgentRunService>().ActivateReattachAsync(reservation!, CancellationToken.None)).ShouldBeNull();
        else (await verify.Resolve<IAgentRunService>().ClaimOwnershipAsync(runId, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_commit_abort_returns_no_owner_and_same_scope_retry_can_claim()
    {
        var runId = await CreateQueuedAsync();
        var fault = new OwnershipCommitFault { BeforeCommit = true };
        using var scope = FaultScope(fault);
        var runs = scope.Resolve<IAgentRunService>();
        var failure = await Should.ThrowAsync<IOException>(() => runs.ClaimOwnershipAsync(runId, CancellationToken.None));
        failure.Message.ShouldBe(OwnershipCommitFault.Failure);
        var prior = await runs.GetAsync(runId, CancellationToken.None);
        prior.OwnerId.ShouldBeNull();
        prior.Status.ShouldBe(AgentRunStatus.Queued);
        var owner = (await runs.ClaimOwnershipAsync(runId, CancellationToken.None))!;
        owner.Epoch.ShouldBe(1);
        await runs.AssertOwnershipAsync(owner, CancellationToken.None);
    }

    [Fact]
    public async Task Ack_recovery_cannot_adopt_the_owner_that_replaced_its_committed_claim()
    {
        var runId = await CreateQueuedAsync();
        AgentRunOwnerToken? replacement = null;
        var fault = new OwnershipCommitFault { AfterCommit = async () =>
        {
            using var other = _fixture.BeginScope();
            await ExpireAsync(other, runId);
            var runs = other.Resolve<IAgentRunService>();
            var reservation = (await runs.ReserveReattachAsync(runId, CancellationToken.None))!;
            replacement = await runs.ActivateReattachAsync(reservation, CancellationToken.None);
        } };
        using var scope = FaultScope(fault);
        var failure = await Should.ThrowAsync<IOException>(() => scope.Resolve<IAgentRunService>().ClaimOwnershipAsync(runId, CancellationToken.None));
        failure.Message.ShouldBe(OwnershipCommitFault.Failure);
        replacement.ShouldNotBeNull();
        await scope.Resolve<IAgentRunService>().AssertOwnershipAsync(replacement, CancellationToken.None);
    }

    [Fact]
    public async Task Unknown_ack_is_bounded_and_throws_instead_of_returning_an_unconfirmed_owner()
    {
        var runId = await CreateQueuedAsync();
        using var holderScope = _fixture.BeginScope();
        var holder = holderScope.Resolve<CodeSpaceDbContext>();
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? held = null;
        var fault = new OwnershipCommitFault { AfterCommit = async () =>
        {
            held = await holder.Database.BeginTransactionAsync();
            await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM agent_run WHERE id = {runId} FOR UPDATE");
        } };
        using var scope = FaultScope(fault);
        var elapsed = Stopwatch.StartNew();
        try
        {
            var failure = await Should.ThrowAsync<IOException>(() => scope.Resolve<IAgentRunService>().ClaimOwnershipAsync(runId, CancellationToken.None));
            failure.Message.ShouldBe(OwnershipCommitFault.Failure);
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), "recovery has its own five-second deadline and cannot wait forever for the claim row");
        }
        finally { if (held != null) await held.DisposeAsync(); }
        var runs = scope.Resolve<IAgentRunService>();
        var prior = await runs.GetAsync(runId, CancellationToken.None);
        prior.OwnerId.ShouldNotBeNull();
        prior.Status.ShouldBe(AgentRunStatus.Running);
        (await runs.ClaimOwnershipAsync(runId, CancellationToken.None)).ShouldBeNull();
        await ExpireAsync(scope, runId);
        var reservation = (await runs.ReserveReattachAsync(runId, CancellationToken.None))!;
        (await runs.ActivateReattachAsync(reservation, CancellationToken.None))!.OwnerId.ShouldNotBe(prior.OwnerId.Value);
    }

    [Fact]
    public async Task A_terminal_write_with_lost_ack_preserves_the_result_but_does_not_reauthorize_completion()
    {
        var runId = await CreateQueuedAsync();
        var fault = new TerminalCommitAcknowledgementFault();
        using var scope = FaultScope(fault);
        var runs = scope.Resolve<IAgentRunService>();
        var owner = (await runs.ClaimOwnershipAsync(runId, CancellationToken.None))!;
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "durable first result" };
        fault.Armed = true;
        await Should.ThrowAsync<IOException>(() => runs.CompleteAsync(owner, result, CancellationToken.None));
        fault.Armed.ShouldBeFalse();
        var terminal = await runs.GetAsync(runId, CancellationToken.None);
        terminal.Status.ShouldBe(AgentRunStatus.Succeeded);
        terminal.ResultJson.ShouldContain("durable first result");
        await Should.ThrowAsync<AgentRunOwnershipLostException>(() => runs.CompleteAsync(owner, result with { Summary = "must not overwrite" }, CancellationToken.None));
        var after = await runs.GetAsync(runId, CancellationToken.None);
        after.ResultJson.ShouldBe(terminal.ResultJson);
        after.CompletedAt.ShouldBe(terminal.CompletedAt);
        // This slice does not promise a terminal acknowledgement/notification receipt; recovery must read durable terminal state.
    }

    private sealed class TerminalCommitAcknowledgementFault : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!Armed || !command.CommandText.Contains("completed_at = clock_timestamp()", StringComparison.Ordinal)) return ValueTask.FromResult(result);
            Armed = false;
            throw new IOException("Terminal write committed but its acknowledgement was lost");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_heartbeat_never_renews_and_an_administrative_cancel_revokes_the_observer(bool administrative)
    {
        var runId = await CreateQueuedAsync();
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var owner = (await runs.ClaimOwnershipAsync(runId, CancellationToken.None))!;
        if (administrative) (await runs.CancelRunningAsync(runId, "operator cancelled", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None)).ShouldBeTrue();
        else await runs.CompleteAsync(owner, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None);
        var terminal = await runs.GetAsync(runId, CancellationToken.None);
        if (administrative) await Should.ThrowAsync<AgentRunOwnershipLostException>(() => runs.HeartbeatAsync(owner, CancellationToken.None));
        else await runs.HeartbeatAsync(owner, CancellationToken.None);
        var after = await runs.GetAsync(runId, CancellationToken.None);
        after.HeartbeatAt.ShouldBe(terminal.HeartbeatAt);
        after.LeaseExpiresAt.ShouldBe(terminal.LeaseExpiresAt);
        after.FenceEpoch.ShouldBe(administrative ? owner.Epoch + 1 : owner.Epoch);
    }

    private ILifetimeScope FaultScope(IInterceptor fault) => _fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(fault).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private sealed class OwnershipCommitFault : DbTransactionInterceptor
    {
        public const string Failure = "Injected ownership commit acknowledgement failure";
        public bool BeforeCommit { get; init; }
        public Func<Task>? AfterCommit { get; init; }
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!BeforeCommit || Fired) return ValueTask.FromResult(result);
            Fired = true;
            throw new IOException(Failure);
        }

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (BeforeCommit || Fired) return;
            Fired = true;
            if (AfterCommit is not null) await AfterCommit();
            throw new IOException(Failure);
        }
    }

    private static Task ExpireAsync(ILifetimeScope scope, Guid runId) => scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");

    private async Task<Guid> CreateQueuedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        return (await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "Check ownership", Harness = "codex-cli", Model = "test" }, teamId, null, null, cancellationToken: CancellationToken.None)).Id;
    }

    private async Task<Guid> CreateLegacyRunningAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var service = scope.Resolve<IAgentRunService>();
        var run = await service.CreateAsync(new AgentTask { Goal = "Check ownership", Harness = "codex-cli", Model = "test" }, teamId, null, null, cancellationToken: CancellationToken.None);
        await service.MarkRunningAsync(run.Id, CancellationToken.None);
        return run.Id;
    }
}
