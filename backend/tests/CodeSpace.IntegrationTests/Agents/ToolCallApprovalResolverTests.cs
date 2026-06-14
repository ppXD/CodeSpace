using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (high fidelity — REAL ToolCallApprovalResolver resolved through CodeSpaceModule's DI against real
/// Postgres). Pins the durable-HITL approve/reject decision substrate: approve STAMPS approved_by/approved_at while the
/// row stays AwaitingApproval (the handler flips it later) and signals the waiter Approved; reject drives
/// AwaitingApproval → Failed with an audit Error and signals Rejected; both team-scope every read (a foreign team or a
/// missing token finds nothing → NoWait, row untouched); an already-terminal row is AlreadyResolved; two concurrent
/// approves yield exactly one Resumed + one stamp (the approved_at == null CAS guard); an unknown responseKey is a
/// fail-safe NoWait that never approves. The IToolApprovalWaiterRegistry is the real singleton from the container.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ToolCallApprovalResolverTests
{
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly PostgresFixture _fixture;

    public ToolCallApprovalResolverTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Approve_stamps_the_decision_keeps_awaiting_signals_the_waiter_and_resumes()
    {
        var teamId = await SeedTeamAsync();
        var token = NewToken();
        var actor = Guid.NewGuid();
        var ledgerId = await SeedAwaitingApprovalAsync(teamId, token);

        using var scope = _fixture.BeginScope();
        var waiter = scope.Resolve<IToolApprovalWaiterRegistry>().Register(ledgerId);

        var result = await Resolver(scope).ResolveByTokenAsync(token, "approve", actor, teamId, CancellationToken.None);

        result.ShouldBe(ActionResumeResult.Resumed, "the approve recorded the decision and resumed");
        (await waiter.Completion).ShouldBe(ToolApprovalOutcome.Approved, "the blocked handler is woken with Approved");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "approve stamps the decision but leaves the row AwaitingApproval — the handler flips it to terminal once it runs the side effect");
        row.ApprovedByUserId.ShouldBe(actor);
        row.ApprovedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Reject_fails_the_row_with_an_audit_error_signals_the_waiter_and_resumes()
    {
        var teamId = await SeedTeamAsync();
        var token = NewToken();
        var actor = Guid.NewGuid();
        var ledgerId = await SeedAwaitingApprovalAsync(teamId, token);

        using var scope = _fixture.BeginScope();
        var waiter = scope.Resolve<IToolApprovalWaiterRegistry>().Register(ledgerId);

        var result = await Resolver(scope).ResolveByTokenAsync(token, "reject", actor, teamId, CancellationToken.None);

        result.ShouldBe(ActionResumeResult.Resumed);
        (await waiter.Completion).ShouldBe(ToolApprovalOutcome.Rejected, "the blocked handler is woken with Rejected");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Failed, "reject drives AwaitingApproval → Failed (no side effect to run)");
        row.Error.ShouldNotBeNull();
        row.Error!.ShouldContain("rejected", customMessage: "the failure reason records the rejection");
        row.ApprovedAt.ShouldBeNull("a rejected call was never approved");
    }

    [Fact]
    public async Task A_foreign_team_finds_nothing_and_leaves_the_row_untouched()
    {
        var ownerTeam = await SeedTeamAsync();
        var foreignTeam = await SeedTeamAsync();
        var token = NewToken();
        var ledgerId = await SeedAwaitingApprovalAsync(ownerTeam, token);

        using var scope = _fixture.BeginScope();

        var result = await Resolver(scope).ResolveByTokenAsync(token, "approve", Guid.NewGuid(), foreignTeam, CancellationToken.None);

        result.ShouldBe(ActionResumeResult.NoWait, "a cross-team token finds no parked approval for that team");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the foreign team's response never touched the owner's row");
        row.ApprovedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_missing_token_is_NoWait()
    {
        var teamId = await SeedTeamAsync();

        using var scope = _fixture.BeginScope();

        (await Resolver(scope).ResolveByTokenAsync(NewToken(), "approve", Guid.NewGuid(), teamId, CancellationToken.None))
            .ShouldBe(ActionResumeResult.NoWait, "no parked approval exists for this token");
    }

    [Fact]
    public async Task An_already_terminal_row_is_AlreadyResolved()
    {
        var teamId = await SeedTeamAsync();
        var token = NewToken();
        await SeedAwaitingApprovalAsync(teamId, token, status: ToolCallLedgerStatus.Succeeded);

        using var scope = _fixture.BeginScope();

        (await Resolver(scope).ResolveByTokenAsync(token, "approve", Guid.NewGuid(), teamId, CancellationToken.None))
            .ShouldBe(ActionResumeResult.AlreadyResolved, "a row that already moved past AwaitingApproval rejects the late click");
    }

    [Fact]
    public async Task Two_concurrent_approves_yield_exactly_one_resumed_and_one_stamp()
    {
        var teamId = await SeedTeamAsync();
        var token = NewToken();
        var ledgerId = await SeedAwaitingApprovalAsync(teamId, token);

        async Task<ActionResumeResult> ApproveAsync()
        {
            using var scope = _fixture.BeginScope();
            return await Resolver(scope).ResolveByTokenAsync(token, "approve", Guid.NewGuid(), teamId, CancellationToken.None);
        }

        var results = await Task.WhenAll(ApproveAsync(), ApproveAsync());

        results.Count(r => r == ActionResumeResult.Resumed).ShouldBe(1, "exactly one approve wins the approved_at == null CAS");
        results.Count(r => r == ActionResumeResult.AlreadyResolved).ShouldBe(1, "the loser sees the stamp already set and reports AlreadyResolved");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval);
        row.ApprovedAt.ShouldNotBeNull("exactly one stamp landed");
        row.ApprovedByUserId.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_unknown_response_key_is_NoWait_and_never_approves()
    {
        var teamId = await SeedTeamAsync();
        var token = NewToken();
        var ledgerId = await SeedAwaitingApprovalAsync(teamId, token);

        using var scope = _fixture.BeginScope();
        var waiter = scope.Resolve<IToolApprovalWaiterRegistry>().Register(ledgerId);

        var result = await Resolver(scope).ResolveByTokenAsync(token, "maybe", Guid.NewGuid(), teamId, CancellationToken.None);

        result.ShouldBe(ActionResumeResult.NoWait, "a non approve/reject key records the response without resolving the approval");

        waiter.Completion.IsCompleted.ShouldBeFalse("the fail-safe never signals the waiter");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the row is untouched — the fail-safe never approves or fails it");
        row.ApprovedAt.ShouldBeNull();
    }

    private static IToolCallApprovalResolver Resolver(ILifetimeScope scope) => scope.Resolve<IToolCallApprovalResolver>();

    private static string NewToken() => $"tok-{Guid.NewGuid():N}";

    private async Task<ToolCallLedger> ReadRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    private async Task<Guid> SeedAwaitingApprovalAsync(Guid teamId, string token, ToolCallLedgerStatus status = ToolCallLedgerStatus.AwaitingApproval)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = id,
            TeamId = teamId,
            AgentRunId = Guid.NewGuid(),
            ToolKind = "git.open_pr",
            IdempotencyKey = $"git.open_pr:{id:N}",
            InputHash = InputHash,
            Status = status,
            ApprovalToken = token,
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"approval-{userId:N}@test.local", Name = $"approval-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"approval-{teamId:N}", Name = "Approval Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
