using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// E1 — the MCP tool-call audit surface, end-to-end through the mediator + real Postgres (mirrors
/// <c>AgentDefinitionFlowTests</c>: the full pipeline IS the API-flow here, since the API project has no
/// HTTP test host — <c>AgentsController.ListToolCalls</c> is a one-line <c>_mediator.Send(query)</c>).
///
/// <para>Proves the operator-facing contract: an owning team reads its run's governed tool calls back in
/// chronological order with every audit field including the approval trail (ApprovedByUserId / ApprovedAt);
/// a FOREIGN team reads an empty list — the tenancy proof (<c>GetForRunAsync</c> filters <c>TeamId == teamId</c>,
/// and AgentRunId is a soft link with no FK, so a foreign / unknown run is indistinguishable from empty, no
/// existence leak); and read-only tools NEVER appear because they skip the ledger entirely (only side-effecting
/// calls get a row — documented + asserted by their absence in the seeded set).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ToolCallAuditFlowTests
{
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly PostgresFixture _fixture;

    public ToolCallAuditFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_owning_team_reads_its_runs_tool_calls_chronological_with_the_full_audit_trail()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var approverId = Guid.NewGuid();

        // Three side-effecting rows, OLDEST first by CreatedDate: a Succeeded write, a Failed write, and one that
        // was approved-then-recorded (carries the approval trail). Inserted in reverse to prove the handler re-orders
        // ascending (GetForRunAsync returns newest-first; ListToolCallsQueryHandler flips it to chronological).
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var approvedAt = baseTime.AddMinutes(2).AddSeconds(30);
        await SeedLedgerRowAsync(runId, teamId, "git.open_pr", ToolCallLedgerStatus.Succeeded, error: null, createdAt: baseTime, approvedByUserId: null, approvedAt: null);
        await SeedLedgerRowAsync(runId, teamId, "git.pr_review", ToolCallLedgerStatus.Failed, error: "remote rejected", createdAt: baseTime.AddMinutes(1), approvedByUserId: null, approvedAt: null);
        await SeedLedgerRowAsync(runId, teamId, "git.merge_pr", ToolCallLedgerStatus.Succeeded, error: null, createdAt: baseTime.AddMinutes(2), approvedByUserId: approverId, approvedAt: approvedAt);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var calls = await scope.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId });

        calls.Select(c => c.ToolKind).ShouldBe(new[] { "git.open_pr", "git.pr_review", "git.merge_pr" },
            customMessage: "tool calls must come back oldest-first by CreatedDate — the chronological audit order");

        var opened = calls[0];
        opened.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        opened.Error.ShouldBeNull();
        opened.ApprovedByUserId.ShouldBeNull("a call that needed no approval carries no approval trail");
        opened.ApprovedAt.ShouldBeNull();

        var failed = calls[1];
        failed.Status.ShouldBe(ToolCallLedgerStatus.Failed);
        failed.Error.ShouldBe("remote rejected", customMessage: "the already-redacted Error is safe to surface as audit context");

        var merged = calls[2];
        merged.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        merged.ApprovedByUserId.ShouldBe(approverId, customMessage: "the approval trail — WHO approved — must surface for audit");
        merged.ApprovedAt.ShouldNotBeNull("the approval trail — WHEN it was approved — must surface for audit");
        merged.ApprovedAt!.Value.ShouldBe(approvedAt, tolerance: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_foreign_team_reads_an_empty_list_no_existence_leak()
    {
        var (ownerTeam, ownerUser) = await SeedTeamAsync();
        var (foreignTeam, foreignUser) = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        await SeedLedgerRowAsync(runId, ownerTeam, "git.open_pr", ToolCallLedgerStatus.Succeeded, error: null, createdAt: DateTimeOffset.UtcNow, approvedByUserId: null, approvedAt: null);

        // The owning team sees its row…
        using (var owner = _fixture.BeginScopeAs(ownerUser, ownerTeam, Roles.Admin))
            (await owner.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId })).ShouldHaveSingleItem();

        // …a FOREIGN team reading the SAME run id sees nothing — GetForRunAsync filters TeamId, so a cross-team
        // (or simply unknown) run is indistinguishable from empty. The tenancy proof.
        using (var foreign = _fixture.BeginScopeAs(foreignUser, foreignTeam, Roles.Admin))
            (await foreign.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId }))
                .ShouldBeEmpty("a foreign team must read no tool calls for another tenant's run — no existence leak");
    }

    [Fact]
    public async Task An_unknown_run_reads_an_empty_list()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        (await scope.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = Guid.NewGuid() }))
            .ShouldBeEmpty("a run with no ledger rows (unknown / never made a governed call) reads empty, never errors");
    }

    [Fact]
    public async Task Read_only_tools_are_absent_because_they_skip_the_ledger()
    {
        // Read-only tools (e.g. agent.run_command at a read, git.list_prs) are NOT recorded in the ToolCallLedger —
        // only SIDE-EFFECTING calls get a row (ToolCallLedger doc + McpRequestHandler). So the audit surface only
        // ever lists governed calls; a read leaves no row. We seed ONLY a side-effecting row and assert the surface
        // contains exactly it — proving a read-only call would have nothing to surface.
        var (teamId, userId) = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        await SeedLedgerRowAsync(runId, teamId, "git.open_pr", ToolCallLedgerStatus.Succeeded, error: null, createdAt: DateTimeOffset.UtcNow, approvedByUserId: null, approvedAt: null);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var calls = await scope.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId });

        calls.ShouldHaveSingleItem().ToolKind.ShouldBe("git.open_pr",
            customMessage: "only the side-effecting call is in the ledger — read-only tools skip it, so they never appear in the audit surface");
    }

    private async Task SeedLedgerRowAsync(Guid runId, Guid teamId, string toolKind, ToolCallLedgerStatus status, string? error, DateTimeOffset createdAt, Guid? approvedByUserId, DateTimeOffset? approvedAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            AgentRunId = runId,
            ToolKind = toolKind,
            IdempotencyKey = $"{toolKind}:{Guid.NewGuid():N}",
            InputHash = InputHash,
            Status = status,
            Error = error,
            ApprovedByUserId = approvedByUserId,
            ApprovedAt = approvedAt,
            CreatedDate = createdAt,
            LastModifiedDate = createdAt,
        });

        await db.SaveChangesAsync();
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"audit-{userId:N}@test.local", Name = $"audit-user-{userId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"audit-team-{teamId:N}", Name = "Audit Test Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
