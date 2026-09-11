using System.Text.Json;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Commands.Budget;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Budget;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Infrastructure = CodeSpace.IntegrationTests.Workflows.Infrastructure;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): P15-5b-ii — the standing TEAM cap every reservation is admitted against, on top
/// of each run's own.
///
/// <para>The hole this closes: the ledger's only cap sum was per RUN, so a team could launch any number of runs
/// each comfortably under its own modest cap and spend with no ceiling anywhere. These tests are written against
/// exactly that shape — every reservation below is UNDER its run cap, and only the team's cap can refuse it.</para>
///
/// <para>The concurrency test is the one that needs a real database: the guarantee is that two connections cannot
/// each see the other's headroom as free, and it holds only because admission takes a team-keyed advisory lock
/// AFTER the run-keyed one. Remove the team lock and it fails; reverse the order and the two locks deadlock.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TeamCostCapFlowTests
{
    private const string Kind = "agent-attempt";
    private const decimal RunCap = 100m;

    private readonly PostgresFixture _fixture;

    public TeamCostCapFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_second_run_under_its_own_cap_is_still_refused_by_the_team_cap()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedCapAsync(teamId, 10m);
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        var first = await ledger.ReserveAsync(Guid.NewGuid(), teamId, Kind, "r1#a1", 6m, RunCap, "prices-v1", null, null, CancellationToken.None);
        first.Admitted.ShouldBeTrue("6 is far under the run's own 100 cap and under the team's 10");

        // A DIFFERENT run, so its own committed total is zero — the run cap cannot possibly refuse this.
        var second = await ledger.ReserveAsync(Guid.NewGuid(), teamId, Kind, "r2#a1", 6m, RunCap, "prices-v1", null, null, CancellationToken.None);

        second.Admitted.ShouldBeFalse("6 + 6 would commit 12 past the team's 10 — the invariant a per-run cap can never express");
        second.RefusedGrain.ShouldBe(BudgetCapGrain.Team);
        second.Reason.ShouldContain("team cap", customMessage: "the reason must name WHICH cap refused — the remedy for a team cap is not the remedy for a run cap");
        second.Reason.ShouldContain(TeamCostCap.RollingThirtyDays);
    }

    [Fact]
    public async Task A_physical_admission_on_a_second_run_is_also_refused_by_the_team_cap()
    {
        // The physical POST path (AdmitPhysicalAsync) mints its own reservation through a completely separate
        // method from ReserveAsync — a skipped team check there would be the one way to spend past this cap.
        var (teamId, userId) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        await SeedCapAsync(teamId, 10m);

        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "physical-team-cap-fixture", Definition = Infrastructure.WorkflowsTestSeed.MinimalDefinition(), Activations = Array.Empty<WorkflowActivationInput>(), Enabled = true });
        var exhaustingRunId = await Infrastructure.WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var physicalRunId = await Infrastructure.WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var exhausting = await scope.Resolve<IBudgetLedger>().ReserveAsync(exhaustingRunId, teamId, Kind, "physical-precursor", 9m, RunCap, "prices-v1", null, null, CancellationToken.None);
        exhausting.Admitted.ShouldBeTrue("9 is under the team's 10 — this run alone must not be refused");

        var admission = await scope.Resolve<IPhysicalLlmInvocationLedger>().AdmitPhysicalAsync(new PhysicalLlmAdmission
        {
            InvocationId = Guid.NewGuid(), LogicalCallId = Guid.NewGuid(), CandidateId = Guid.NewGuid(), CandidateOrdinal = 1,
            RunId = physicalRunId, TeamId = teamId, Purpose = "physical-team-cap-test", Provider = "synthetic", RequestedModel = "fixture-model",
            EstimateUsd = 3m, CapUsd = RunCap,
            PricingSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, ModelPrice> { ["fixture-model"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m } }),
            PricingVersion = "fixture-price-v1",
        }, CancellationToken.None);

        admission.Admitted.ShouldBeFalse("9 + 3 would commit 12 past the team's 10 — the physical path answers to the same standing cap as any other admission");
        admission.RefusedGrain.ShouldBe(BudgetCapGrain.Team, "a mutation that skips the team check on the physical path must be caught here, not just on ReserveAsync");
    }

    [Fact]
    public async Task A_team_with_no_cap_keeps_the_per_run_ceiling_only()
    {
        // The release must not start refusing runs on a limit nobody configured.
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        foreach (var i in Enumerable.Range(0, 5))
            (await ledger.ReserveAsync(Guid.NewGuid(), teamId, Kind, $"r{i}#a1", 90m, RunCap, "prices-v1", null, null, CancellationToken.None))
                .Admitted.ShouldBeTrue("no team row and no deployment fallback — behaviour is exactly what it was before 5b-ii");
    }

    [Fact]
    public async Task Concurrent_reserves_across_runs_cannot_both_admit_past_the_team_cap()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedCapAsync(teamId, 10m);

        // 8 parallel reserves of 3, each on its OWN run (so the per-run lock serializes nothing) against a team cap
        // of 10 — at most 3 may admit (9 ≤ 10 < 12), whatever the interleaving.
        var admissions = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IBudgetLedger>().ReserveAsync(Guid.NewGuid(), teamId, Kind, $"s{i}", 3m, RunCap, "prices-v1", null, null, CancellationToken.None);
        }));

        admissions.Count(a => a.Admitted).ShouldBe(3, "the team advisory lock serializes admission across runs — mid-wave overshoot is structurally impossible");
        admissions.Where(a => !a.Admitted).ShouldAllBe(a => a.RefusedGrain == BudgetCapGrain.Team);
    }

    [Fact]
    public async Task The_deployment_fallback_applies_only_to_a_team_with_no_row_of_its_own()
    {
        var (fallbackTeam, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (ownRowTeam, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedCapAsync(ownRowTeam, 40m);

        using var settings = RuntimeSettings.Override(current => current with { DeploymentCostCapUsd = 5m });
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        var fallbackRefusal = await ledger.ReserveAsync(Guid.NewGuid(), fallbackTeam, Kind, "fallback", 6m, RunCap, "prices-v1", null, null, CancellationToken.None);
        fallbackRefusal.Admitted.ShouldBeFalse("a team nobody configured still answers to the deployment's ceiling");
        fallbackRefusal.RefusedGrain.ShouldBe(BudgetCapGrain.Deployment, "the grain must say the limit came from the deployment, not from a team cap the operator never set");

        var ownRow = await ledger.ReserveAsync(Guid.NewGuid(), ownRowTeam, Kind, "own-row", 6m, RunCap, "prices-v1", null, null, CancellationToken.None);
        ownRow.Admitted.ShouldBeTrue("a team's OWN row wins outright — clamping it to the deployment floor would make the management endpoint a lie");
    }

    [Fact]
    public async Task Spend_older_than_the_window_no_longer_holds_team_headroom()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedCapAsync(teamId, 10m);
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        (await ledger.ReserveAsync(Guid.NewGuid(), teamId, Kind, "ancient", 9m, RunCap, "prices-v1", null, null, CancellationToken.None)).Admitted.ShouldBeTrue();
        await BackdateAsync(teamId, "ancient", TeamCostCap.RollingThirtyDaysSpan + TimeSpan.FromDays(1));

        var since = DateTimeOffset.UtcNow - TeamCostCap.RollingThirtyDaysSpan;
        (await ledger.CommittedTeamUsdAsync(teamId, since, CancellationToken.None)).ShouldBe(0m, "a reservation from before the window is spend the rolling cap has forgiven");

        (await ledger.ReserveAsync(Guid.NewGuid(), teamId, Kind, "today", 9m, RunCap, "prices-v1", null, null, CancellationToken.None))
            .Admitted.ShouldBeTrue("the window rolled past the old claim, so its headroom is available again");
    }

    [Fact]
    public async Task An_unbudgeted_observability_claim_is_neither_refused_by_nor_counted_against_the_team_cap()
    {
        // An Unbudgeted plane declares no cap at all (P15-5a) and is excluded from every committed sum. The team cap
        // must not change that in either direction: it cannot refuse the row, and the row cannot eat real headroom.
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedCapAsync(teamId, 10m);
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        var unbudgeted = await ledger.ReserveAsync(Guid.NewGuid(), teamId, $"{BudgetKinds.UnbudgetedPrefix}calibration", "u1", 500m, null, "prices-v1", null, null, CancellationToken.None);
        unbudgeted.Admitted.ShouldBeTrue("a null cap records an observability claim that never refuses");

        (await ledger.CommittedTeamUsdAsync(teamId, DateTimeOffset.UtcNow - TeamCostCap.RollingThirtyDaysSpan, CancellationToken.None)).ShouldBe(0m);
        (await ledger.ReserveAsync(Guid.NewGuid(), teamId, Kind, "real", 9m, RunCap, "prices-v1", null, null, CancellationToken.None))
            .Admitted.ShouldBeTrue("the uncapped row must not eat into a real plane's team headroom");
    }

    [Fact]
    public async Task Setting_the_cap_persists_it_and_immediately_binds_the_next_admission()
    {
        var (teamId, userId) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var owner = _fixture.BeginScopeAs(userId, teamId);
        var set = await owner.Resolve<IMediator>().Send(new SetTeamCostCapCommand { CapUsd = 7m });

        set.CapUsd.ShouldBe(7m);
        set.Window.ShouldBe(TeamCostCap.RollingThirtyDays);
        set.Grain.ShouldBe(BudgetCapGrain.Team);
        (await owner.Resolve<IMediator>().Send(new GetTeamCostCapQuery()))!.CapUsd.ShouldBe(7m);

        var refused = await owner.Resolve<IBudgetLedger>().ReserveAsync(Guid.NewGuid(), teamId, Kind, "after-set", 8m, RunCap, "prices-v1", null, null, CancellationToken.None);
        refused.RefusedGrain.ShouldBe(BudgetCapGrain.Team, "a cap an operator just set must bind the very next admission, not the next restart");

        await owner.Resolve<IMediator>().Send(new ClearTeamCostCapCommand());
        (await owner.Resolve<IMediator>().Send(new GetTeamCostCapQuery())).ShouldBeNull("cleared, and no deployment fallback configured");

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().BudgetTeamCap.AnyAsync(cap => cap.TeamId == teamId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Clearing_a_cap_the_team_never_had_is_a_no_op()
    {
        var (teamId, userId) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var owner = _fixture.BeginScopeAs(userId, teamId);

        await owner.Resolve<IMediator>().Send(new ClearTeamCostCapCommand());
    }

    [Theory]
    [InlineData(TeamRole.Viewer, true)]
    [InlineData(TeamRole.Member, true)]
    [InlineData(TeamRole.Admin, false)]
    [InlineData(TeamRole.Owner, false)]
    public async Task Only_an_admin_or_owner_may_move_the_team_cap(TeamRole role, bool denied)
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var actorId = await SeedMemberAsync(teamId, role);

        using var scope = _fixture.BeginScopeAs(actorId, teamId);
        var thrown = await Record.ExceptionAsync(() => scope.Resolve<IMediator>().Send(new SetTeamCostCapCommand { CapUsd = 12m }));

        if (denied)
        {
            thrown.ShouldBeOfType<TenantAccessDeniedException>($"{role} must not hold '{TeamPermissions.BudgetManage}' — moving the cap is moving the team's spending limit")
                .Reason.ShouldContain(TeamPermissions.BudgetManage);

            using var verify = _fixture.BeginScope();
            (await verify.Resolve<CodeSpaceDbContext>().BudgetTeamCap.AnyAsync(cap => cap.TeamId == teamId)).ShouldBeFalse("the denied command must not have written anything");
            return;
        }

        thrown.ShouldBeNull();

        using var confirm = _fixture.BeginScope();
        (await confirm.Resolve<CodeSpaceDbContext>().BudgetTeamCap.SingleAsync(cap => cap.TeamId == teamId)).CapUsd.ShouldBe(12m);
    }

    private async Task SeedCapAsync(Guid teamId, decimal capUsd)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.BudgetTeamCap.Add(new BudgetTeamCap { TeamId = teamId, CapUsd = capUsd, CapWindow = TeamCostCap.RollingThirtyDays });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<Guid> SeedMemberAsync(Guid teamId, TeamRole role)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"cap-{userId:N}@test.local", Name = $"cap-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return userId;
    }

    /// <summary>Pushes a reservation's created_date back by statement — the audit stamp owns it on insert, so a test cannot seed an aged row directly.</summary>
    private async Task BackdateAsync(Guid teamId, string scopeKey, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var backdated = DateTimeOffset.UtcNow - age;

        await db.BudgetReservation.Where(row => row.TeamId == teamId && row.ScopeKey == scopeKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedDate, backdated)).ConfigureAwait(false);
    }
}
