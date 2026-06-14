using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (high fidelity — REAL <see cref="ToolCallLedgerService"/> resolved through the DI container against
/// real Postgres). Pins the D3 reaper CAS: an over-deadline UNDECIDED AwaitingApproval row is durably flipped to
/// Expired (returned with its message id); the not-expire-approved guard is load-bearing — an approved-but-not-yet-
/// executed row (ApprovedAt != null), a not-yet-due row, a terminal row, and a null-deadline row are all left untouched.
/// A second sweep is a no-op for an already-Expired row (idempotent), two teams' due rows both expire (team-agnostic
/// sweep, per-row CAS), and two concurrent sweeps expire each row exactly once (single-winner CAS).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ToolCallLedgerExpiryTests
{
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly PostgresFixture _fixture;

    public ToolCallLedgerExpiryTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Expires_an_over_deadline_undecided_row_and_returns_it_with_its_message_id()
    {
        var teamId = await SeedTeamAsync();
        var messageId = Guid.NewGuid();
        var ledgerId = await SeedAsync(teamId, deadlineAt: Past, approvalMessageId: messageId);

        var now = DateTimeOffset.UtcNow;

        var expired = await ExpireAsync(now);

        var mine = expired.Where(e => e.LedgerId == ledgerId).ToList();
        mine.ShouldHaveSingleItem("the over-deadline undecided row is returned exactly once");
        mine[0].TeamId.ShouldBe(teamId, "the team is carried for log correlation");
        mine[0].ApprovalMessageId.ShouldBe(messageId, "the card message id is carried for the mirror");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Expired, "the undecided over-deadline row is durably Expired");
        row.Error.ShouldBe(ToolCallLedgerService.ApprovalExpiredError, "the audit reason records the deadline expiry");
    }

    [Fact]
    public async Task Does_not_expire_a_not_yet_due_row()
    {
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedAsync(teamId, deadlineAt: Future);

        var expired = await ExpireAsync(DateTimeOffset.UtcNow);

        expired.ShouldNotContain(e => e.LedgerId == ledgerId, "a row whose deadline is still in the future is not yet expired");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval);
    }

    [Fact]
    public async Task Does_not_expire_an_approved_row_even_when_past_the_deadline()
    {
        // The not-expire-approved guard: an approved-but-not-yet-executed row belongs to an in-flight execution claim.
        // Even past its deadline it MUST NOT be expired (ApprovedAt != null fails both the candidate query and the CAS).
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedAsync(teamId, deadlineAt: Past, approvedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var expired = await ExpireAsync(DateTimeOffset.UtcNow);

        expired.ShouldNotContain(e => e.LedgerId == ledgerId, "an approved row is never expired — it belongs to an in-flight execution claim");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the approved row stays AwaitingApproval for the handler to claim + execute");
        row.Error.ShouldBeNull("the approved row was never stamped with the expiry reason");
    }

    [Theory]
    [InlineData(ToolCallLedgerStatus.Succeeded)]
    [InlineData(ToolCallLedgerStatus.Failed)]
    [InlineData(ToolCallLedgerStatus.Denied)]
    public async Task Does_not_expire_a_terminal_row(ToolCallLedgerStatus terminal)
    {
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedAsync(teamId, deadlineAt: Past, status: terminal);

        var expired = await ExpireAsync(DateTimeOffset.UtcNow);

        expired.ShouldNotContain(e => e.LedgerId == ledgerId, "a terminal row is past AwaitingApproval — the candidate query excludes it");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(terminal, "the terminal row is untouched");
    }

    [Fact]
    public async Task Does_not_expire_a_null_deadline_row()
    {
        // A null deadline means no expiry was ever set (e.g. a synchronous Pending path that never parked) — never reap it.
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedAsync(teamId, deadlineAt: null);

        var expired = await ExpireAsync(DateTimeOffset.UtcNow);

        expired.ShouldNotContain(e => e.LedgerId == ledgerId, "a row with no deadline is excluded by the ApprovalDeadlineAt != null predicate");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval);
    }

    [Fact]
    public async Task A_second_sweep_is_a_no_op_for_an_already_expired_row()
    {
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedAsync(teamId, deadlineAt: Past);

        (await ExpireAsync(DateTimeOffset.UtcNow)).ShouldContain(e => e.LedgerId == ledgerId, "the first sweep expires it");

        var second = await ExpireAsync(DateTimeOffset.UtcNow);

        second.ShouldNotContain(e => e.LedgerId == ledgerId, "the second sweep finds it already Expired — idempotent, no re-return");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
    }

    [Fact]
    public async Task Two_teams_due_rows_both_expire()
    {
        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();
        var ledgerA = await SeedAsync(teamA, deadlineAt: Past);
        var ledgerB = await SeedAsync(teamB, deadlineAt: Past);

        var expired = await ExpireAsync(DateTimeOffset.UtcNow);

        expired.ShouldContain(e => e.LedgerId == ledgerA, "the team-agnostic sweep expires team A's due row");
        expired.ShouldContain(e => e.LedgerId == ledgerB, "and team B's due row — each via its own per-row CAS");
        (await ReadRowAsync(ledgerA)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
        (await ReadRowAsync(ledgerB)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
    }

    [Fact]
    public async Task Two_concurrent_sweeps_expire_each_row_exactly_once()
    {
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedAsync(teamId, deadlineAt: Past);

        async Task<IReadOnlyList<ExpiredToolApproval>> SweepAsync()
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IToolCallLedgerService>().ExpireStaleApprovalsAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }

        var results = await Task.WhenAll(SweepAsync(), SweepAsync());

        var winners = results.SelectMany(r => r).Count(e => e.LedgerId == ledgerId);
        winners.ShouldBe(1, "the per-row status-guarded CAS is single-winner — exactly one sweep expires the row");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
    }

    private static DateTimeOffset Past => DateTimeOffset.UtcNow.AddMinutes(-5);
    private static DateTimeOffset Future => DateTimeOffset.UtcNow.AddMinutes(5);

    private async Task<IReadOnlyList<ExpiredToolApproval>> ExpireAsync(DateTimeOffset now)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IToolCallLedgerService>().ExpireStaleApprovalsAsync(now, CancellationToken.None);
    }

    private async Task<ToolCallLedger> ReadRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    private async Task<Guid> SeedAsync(Guid teamId, DateTimeOffset? deadlineAt, ToolCallLedgerStatus status = ToolCallLedgerStatus.AwaitingApproval, DateTimeOffset? approvedAt = null, Guid? approvalMessageId = null)
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
            ApprovalToken = $"tok-{id:N}",
            ApprovalDeadlineAt = deadlineAt,
            ApprovedAt = approvedAt,
            ApprovalMessageId = approvalMessageId,
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"expiry-{userId:N}@test.local", Name = $"expiry-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"expiry-{teamId:N}", Name = "Expiry Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
