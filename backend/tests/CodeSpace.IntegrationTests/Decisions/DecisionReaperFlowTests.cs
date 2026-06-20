using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Decisions;

/// <summary>
/// 🟢 Integration (high fidelity — REAL <see cref="ToolCallLedgerService"/> + <see cref="DecisionExpiryService"/> resolved
/// through DI against real Postgres). The agent-grain decision reaper (Decision substrate D5b, AC4 never-hang): an overdue
/// parked decision.request WITH a default is answered by the default (Succeeded carrying a Timeout DecisionAnswer, NOT the
/// approval Expired terminal); one WITHOUT a default is left Pending (convert-to-human is D5d); a not-yet-due one is
/// untouched; a reaper tick racing a human answer leaves exactly one winner via the shared answer CAS; and the decision
/// reaper + the approval reaper touch DISJOINT rows (D5a's ToolKind split).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DecisionReaperFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly PostgresFixture _fixture;

    public DecisionReaperFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_overdue_decision_with_a_default_is_answered_with_that_default()
    {
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: Past, defaultAction: "a");

        var timedOut = await ReapAsync();

        timedOut.ShouldContain(t => t.LedgerId == ledgerId, "the overdue decision is defaulted exactly once");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "a defaulted decision reaches Succeeded via the answer CAS — NOT the approval Expired terminal (which would surface as an error)");
        row.Error.ShouldBeNull();

        var answer = JsonSerializer.Deserialize<DecisionAnswer>(row.ResultJson!, Json)!;
        answer.AnsweredBy.ShouldBe(DecisionAnsweredByKinds.Timeout);
        answer.SelectedOptions.ShouldBe(new[] { "a" }, "the configured default is the recorded selection");
        answer.TimedOut.ShouldBeTrue();
        answer.Rationale.ShouldNotBeNullOrWhiteSpace("a timeout answer is never silent (AC3)");
    }

    [Fact]
    public async Task An_overdue_no_default_decision_is_converted_to_human_required()
    {
        // D5d: an auto/supervisor decision with no safe DefaultAction times out → CONVERT-TO-HUMAN. It is never defaulted
        // (no blind expire), its policy is re-stamped human_required (durably escalated — the supervisor can never auto-answer
        // it now, and the queue shows it as human-only), and its reaper re-examination is deferred (starvation guard) — all
        // while staying AwaitingApproval for a person.
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: Past, defaultAction: null);   // seeded supervisor_first

        var timedOut = await ReapAsync();

        timedOut.ShouldNotContain(t => t.LedgerId == ledgerId, "a no-default decision is not defaulted by the reaper");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "it stays parked for a human");
        row.ApprovalDeadlineAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "its reaper re-examination is deferred so it leaves the overdue set — no starvation of defaultable rows");

        var envelope = JsonSerializer.Deserialize<DecisionRequest>(row.DecisionEnvelopeJson!, Json)!;
        envelope.Policy.ShouldBe(DecisionPolicies.HumanRequired, "the decision is durably escalated to human-only (convert-to-human)");
    }

    [Fact]
    public async Task A_human_only_decision_with_a_default_is_converted_not_defaulted()
    {
        // The floor wins over a configured default: a decision the floor reserves for a person (human_required) is NEVER
        // auto-resolved on timeout EVEN IF it carries a DefaultAction — the default would defeat the floor's "a human must
        // decide". It stays AwaitingApproval, never Succeeded.
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: Past, defaultAction: "a", policy: DecisionPolicies.HumanRequired);

        var timedOut = await ReapAsync();

        timedOut.ShouldNotContain(t => t.LedgerId == ledgerId, "a human-only decision is never auto-defaulted, even with a DefaultAction");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the floor wins — it stays parked for a human, not Succeeded-by-default");
    }

    [Fact]
    public async Task A_malformed_envelope_is_deferred_never_blind_expired()
    {
        // A valid-jsonb-but-wrong-shape envelope (missing the required DecisionRequest members → a JsonException on
        // deserialize) has no readable default → treated exactly like no-default: left AwaitingApproval, deferred, never
        // expired-as-an-error. Proves the never-blind-expire guarantee for a corrupt envelope, not just an absent default.
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedRawDecisionAsync(teamId, deadlineAt: Past, envelopeJson: """{"foo":"bar"}""");

        var timedOut = await ReapAsync();

        timedOut.ShouldNotContain(t => t.LedgerId == ledgerId);
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "a corrupt envelope is never blind-expired — it stays for a human");
    }

    [Fact]
    public async Task The_reaper_wakes_a_blocked_decision_call_with_Approved_not_expired()
    {
        // The load-bearing D5b invariant: a defaulted decision wakes the blocked same-pod call with Approved (the decision
        // WAS answered, by timeout → the call reads the default answer), NOT Expired (the approval grain's "no decision"
        // terminal → would surface as an error). Driven through the real DecisionExpiryService over real Postgres.
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: Past, defaultAction: "a");

        using var scope = _fixture.BeginScope();
        var waiter = scope.Resolve<IToolApprovalWaiterRegistry>().Register(ledgerId);

        (await scope.Resolve<IDecisionExpiryService>().ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        (await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(ToolApprovalOutcome.Approved,
            customMessage: "the same-pod blocked decision call is woken with Approved (the decision was answered by default) — NOT Expired; check DecisionExpiryService.ResolveAsync.TrySignal");
    }

    [Fact]
    public async Task Two_concurrent_reaper_sweeps_default_a_row_exactly_once()
    {
        // The single-winner answer CAS across two reaper pods: a single overdue defaulted row is defaulted exactly once.
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: Past, defaultAction: "a");

        async Task<IReadOnlyList<TimedOutDecision>> SweepAsync()
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IToolCallLedgerService>().ExpireStaleDecisionsAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }

        var results = await Task.WhenAll(SweepAsync(), SweepAsync());

        results.SelectMany(r => r).Count(t => t.LedgerId == ledgerId).ShouldBe(1, "the per-row answer CAS is single-winner — exactly one sweep defaults the row");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
    }

    [Fact]
    public async Task A_not_yet_due_decision_is_untouched()
    {
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: Future, defaultAction: "a");

        await ReapAsync();

        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "a decision whose deadline is still in the future is not yet defaulted");
    }

    [Fact]
    public async Task A_human_answer_and_a_reaper_tick_leave_exactly_one_winner()
    {
        // The shared single-winner answer CAS: a human answering AND a reaper tick must resolve the decision once. Here the
        // human answers first (Succeeded with their choice); the reaper then finds it already resolved → no-op, no overwrite.
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: Past, defaultAction: "a");

        using (var scope = _fixture.BeginScope())
        {
            var humanAnswer = JsonSerializer.Serialize(new DecisionAnswer { DecisionId = ledgerId, AnsweredBy = DecisionAnsweredByKinds.Human, SelectedOptions = new[] { "b" } }, Json);
            (await scope.Resolve<IToolCallLedgerService>().TryAnswerDecisionAsync(ledgerId, teamId, humanAnswer, CancellationToken.None)).ShouldBeTrue("the human wins the CAS");
        }

        var timedOut = await ReapAsync();

        timedOut.ShouldNotContain(t => t.LedgerId == ledgerId, "the reaper finds the decision already resolved — it does not default it");
        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        JsonSerializer.Deserialize<DecisionAnswer>(row.ResultJson!, Json)!.SelectedOptions.ShouldBe(new[] { "b" }, "the human's answer stands — the reaper never overwrote it");
    }

    [Fact]
    public async Task The_decision_reaper_and_the_approval_reaper_touch_disjoint_rows()
    {
        // D5a's ToolKind split, both directions: the approval reaper expires the git.open_pr approval but NOT the decision;
        // the decision reaper defaults the decision but NOT the approval. Two reapers over one table, never fighting.
        var teamId = await SeedTeamAsync();
        var approvalId = await SeedApprovalAsync(teamId, deadlineAt: Past);
        var decisionId = await SeedDecisionAsync(teamId, deadlineAt: Past, defaultAction: "a");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IToolCallLedgerService>().ExpireStaleApprovalsAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        (await ReadRowAsync(approvalId)).Status.ShouldBe(ToolCallLedgerStatus.Expired, "the approval reaper expired the real approval");
        (await ReadRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "and left the decision untouched (D5a)");

        await ReapAsync();

        (await ReadRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the decision reaper defaulted the decision");
        (await ReadRowAsync(approvalId)).Status.ShouldBe(ToolCallLedgerStatus.Expired, "and left the (already-expired) approval untouched");
    }

    [Fact]
    public async Task ExpireDueAsync_via_the_service_defaults_the_decision_and_returns_the_count()
    {
        // Drive the orchestrating service (the recurring job's handler hands off to it) end-to-end over real Postgres.
        var teamId = await SeedTeamAsync();
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: Past, defaultAction: "a");

        int count;
        using (var scope = _fixture.BeginScope())
            count = await scope.Resolve<IDecisionExpiryService>().ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        count.ShouldBeGreaterThanOrEqualTo(1, "the service returns the count durably defaulted");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
    }

    // ─── Drive the real services ────────────────────────────────────────────────────

    private static DateTimeOffset Past => DateTimeOffset.UtcNow.AddMinutes(-5);
    private static DateTimeOffset Future => DateTimeOffset.UtcNow.AddMinutes(5);

    private async Task<IReadOnlyList<TimedOutDecision>> ReapAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IToolCallLedgerService>().ExpireStaleDecisionsAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private async Task<ToolCallLedger> ReadRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    // ─── Seeding ──────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedDecisionAsync(Guid teamId, DateTimeOffset deadlineAt, string? defaultAction, string policy = DecisionPolicies.SupervisorFirst)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ledgerId = Guid.NewGuid();
        var envelope = new DecisionRequest
        {
            Id = Guid.NewGuid(),
            RootTraceId = Guid.NewGuid(),
            AgentRunId = Guid.NewGuid(),
            Scope = DecisionScopes.Agent,
            RequesterType = DecisionRequesterTypes.Agent,
            DecisionType = DecisionTypes.ChooseOne,
            Question = "which migration path?",
            Options = new[] { new DecisionOption { Id = "a", Label = "A" }, new DecisionOption { Id = "b", Label = "B" } },
            RecommendedOption = "a",
            BlockingReason = "the agent is blocked",
            RiskLevel = DecisionRiskLevels.Low,
            Policy = policy,
            DefaultAction = defaultAction,
            TimeoutAt = deadlineAt,
            DedupeKey = Guid.NewGuid().ToString("N"),
            ResumeBackend = DecisionResumeBackends.ToolLedger,
        };

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId,
            TeamId = teamId,
            AgentRunId = envelope.AgentRunId!.Value,
            ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}",
            InputHash = InputHash,
            Status = ToolCallLedgerStatus.AwaitingApproval,
            ApprovalDeadlineAt = deadlineAt,
            DecisionEnvelopeJson = JsonSerializer.Serialize(envelope, Json),
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return ledgerId;
    }

    private async Task<Guid> SeedRawDecisionAsync(Guid teamId, DateTimeOffset deadlineAt, string envelopeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ledgerId = Guid.NewGuid();
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId,
            TeamId = teamId,
            AgentRunId = Guid.NewGuid(),
            ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}",
            InputHash = InputHash,
            Status = ToolCallLedgerStatus.AwaitingApproval,
            ApprovalDeadlineAt = deadlineAt,
            DecisionEnvelopeJson = envelopeJson,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return ledgerId;
    }

    private async Task<Guid> SeedApprovalAsync(Guid teamId, DateTimeOffset deadlineAt)
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
            Status = ToolCallLedgerStatus.AwaitingApproval,
            ApprovalToken = $"tok-{id:N}",
            ApprovalDeadlineAt = deadlineAt,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"reaper-{userId:N}@test.local", Name = $"reaper-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"reaper-{teamId:N}", Name = "Decision Reaper Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
