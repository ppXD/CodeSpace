using System.Data.Common;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Decisions;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task A_decision_the_sweep_cannot_settle_leaves_the_ones_it_settled_woken_and_mirrored()
    {
        // Each decision's default commits on its own. A fault on a later row used to escape the loop before the sweep
        // returned what it had settled, so an earlier row's parked call was never woken and its card stayed Open — and the
        // next tick selects only rows still pending, so nothing ever mirrored it.
        var (teamId, ownerId) = await SeedTeamWithOwnerAsync();
        var settledCard = await SeedDecisionCardAsync(teamId, ownerId);
        var faultedCard = await SeedDecisionCardAsync(teamId, ownerId);
        var settled = await SeedDecisionAsync(teamId, deadlineAt: LongAgo, defaultAction: "a", approvalMessageId: settledCard);
        var faulted = await SeedDecisionAsync(teamId, deadlineAt: LongAgo.AddSeconds(1), defaultAction: "a", approvalMessageId: faultedCard);

        using var waiters = _fixture.BeginScope();
        var registry = waiters.Resolve<IToolApprovalWaiterRegistry>();
        var settledCall = registry.Register(settled);
        var faultedCall = registry.Register(faulted);

        try
        {
            (await Record.ExceptionAsync(() => SweepThroughAsync(new FailLedgerWriteOf(faulted)))).ShouldBeNull("one decision's fault is that decision's, not the tick's");

            (await WokenAsync(settledCall, "the settled decision's parked call")).ShouldBe(ToolApprovalOutcome.Approved, customMessage: "it is woken with its default — check ExpireStaleDecisionsAsync hands back the rows it won");
            (await CardStateAsync(settledCard)).ShouldBe(InteractionState.Resolved, "and its card is mirrored");

            (await ReadRowAsync(faulted)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the faulted decision stays pending for the next tick");
            faultedCall.Completion.IsCompleted.ShouldBeFalse("nothing settled it, so nothing woke its call");
            (await CardStateAsync(faultedCard)).ShouldBe(InteractionState.Open);

            await SweepThroughAsync();

            (await WokenAsync(faultedCall, "the faulted decision's parked call, on the next tick")).ShouldBe(ToolApprovalOutcome.Approved, customMessage: "the next tick settles what the fault left pending");
            (await CardStateAsync(faultedCard)).ShouldBe(InteractionState.Resolved, "and mirrors its card");
        }
        finally
        {
            registry.Remove(settled);
            registry.Remove(faulted);
        }
    }

    [Fact]
    public async Task A_sweep_stopped_part_way_leaves_the_defaults_it_committed_woken_and_mirrored()
    {
        // A decision's follow-ups run right after its default commits, not once the sweep is done. A sweep stopped part-way
        // — its host shutting down under it — used to take the woken call and the mirrored card of every default it had
        // already committed with it, and the next tick selects only rows still pending, so nothing ever came back for them.
        var (teamId, ownerId) = await SeedTeamWithOwnerAsync();
        var settledCard = await SeedDecisionCardAsync(teamId, ownerId);
        var settled = await SeedDecisionAsync(teamId, deadlineAt: LongAgo, defaultAction: "a", approvalMessageId: settledCard);
        var cutOff = await SeedDecisionAsync(teamId, deadlineAt: LongAgo.AddSeconds(1), defaultAction: "a");

        using var waiters = _fixture.BeginScope();
        var registry = waiters.Resolve<IToolApprovalWaiterRegistry>();
        var settledCall = registry.Register(settled);
        using var shutdown = new CancellationTokenSource();

        try
        {
            (await Record.ExceptionAsync(() => SweepThroughAsync(shutdown.Token, new StopBeforeLedgerWriteOf(cutOff, shutdown)))).ShouldBeAssignableTo<OperationCanceledException>("precondition: the sweep was stopped at its second decision");

            (await WokenAsync(settledCall, "the parked call of the decision defaulted before the stop")).ShouldBe(ToolApprovalOutcome.Approved, customMessage: "its follow-ups ran before the sweep moved on");
            (await CardStateAsync(settledCard)).ShouldBe(InteractionState.Resolved, "and its card was mirrored then too");
            (await ReadRowAsync(cutOff)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the decision the stop cut off is the next tick's");

            await SweepThroughAsync(CancellationToken.None);

            (await ReadRowAsync(cutOff)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "and the next tick defaults it");
        }
        finally
        {
            registry.Remove(settled);
        }
    }

    [Fact]
    public async Task A_card_mirror_that_fails_to_save_leaves_the_tick_ending_cleanly_with_its_count()
    {
        // The sweep's command runs outside a transaction and refuses to end with tracked changes unsaved. A card mirror
        // whose save failed used to leave the card's change tracked, so a tick that had committed every default then
        // failed as a whole.
        var (teamId, ownerId) = await SeedTeamWithOwnerAsync();
        var card = await SeedDecisionCardAsync(teamId, ownerId);
        var ledgerId = await SeedDecisionAsync(teamId, deadlineAt: LongAgo, defaultAction: "a", approvalMessageId: card);

        ExpireStaleDecisionsResponse? response = null;
        (await Record.ExceptionAsync(async () => response = await SweepThroughAsync(new FailCardSaveOf(card)))).ShouldBeNull("a best-effort mirror that failed leaves nothing tracked for the tick to trip on");

        response.ShouldNotBeNull().Defaulted.ShouldBeGreaterThanOrEqualTo(1, "the tick reports the decisions it defaulted");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the default stands");
        (await CardStateAsync(card)).ShouldBe(InteractionState.Open, "only the display mirror was lost");
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
            return await scope.Resolve<IToolCallLedgerService>().ExpireStaleDecisionsAsync(DateTimeOffset.UtcNow, CancellationToken.None).ToListAsync();
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

    /// <summary>Overdue ahead of anything else overdue in this shared database, so the sweep reaches these rows first.</summary>
    private static readonly DateTimeOffset LongAgo = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One tick of the sweep as its recurring job sends it — through the mediator, so the command's own transaction boundary and unsaved-changes check apply — in a scope whose commands pass <paramref name="interceptors"/>.</summary>
    private Task<ExpireStaleDecisionsResponse> SweepThroughAsync(params IInterceptor[] interceptors) => SweepThroughAsync(CancellationToken.None, interceptors);

    /// <summary>As <see cref="SweepThroughAsync(IInterceptor[])"/>, on <paramref name="cancellationToken"/> — the token a host shutting down cancels.</summary>
    private async Task<ExpireStaleDecisionsResponse> SweepThroughAsync(CancellationToken cancellationToken, params IInterceptor[] interceptors)
    {
        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(interceptors).Options;

        using var scope = _fixture.BeginScope(b => b.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance());
        return await scope.Resolve<IMediator>().Send(new ExpireStaleDecisionsCommand(), cancellationToken);
    }

    /// <summary>Stops the sweep just before its write to one decision's row, the way a host shutting down cancels the tick under it.</summary>
    private sealed class StopBeforeLedgerWriteOf : DbCommandInterceptor
    {
        private readonly Guid _ledgerId;
        private readonly CancellationTokenSource _shutdown;

        public StopBeforeLedgerWriteOf(Guid ledgerId, CancellationTokenSource shutdown)
        {
            _ledgerId = ledgerId;
            _shutdown = shutdown;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("UPDATE tool_call_ledger", StringComparison.Ordinal) || !Carries(command, _ledgerId)) return ValueTask.FromResult(result);

            _shutdown.Cancel();
            throw new OperationCanceledException(_shutdown.Token);
        }
    }

    /// <summary>The outcome <paramref name="signal"/> was woken with; fails by its name when it is not woken within five seconds.</summary>
    private static async Task<ToolApprovalOutcome> WokenAsync(IToolApprovalWaiter call, string signal)
    {
        var first = await Task.WhenAny(call.Completion, Task.Delay(TimeSpan.FromSeconds(5)));

        (first == call.Completion).ShouldBeTrue($"{signal} was not woken within 5s — check DecisionExpiryService.ResolveAsync reached it");

        return await call.Completion;
    }

    /// <summary>Fails the sweep's write to one decision's row, once, the way a lost connection or a command timeout would.</summary>
    private sealed class FailLedgerWriteOf : DbCommandInterceptor
    {
        private readonly Guid _ledgerId;
        private int _fired;

        public FailLedgerWriteOf(Guid ledgerId) { _ledgerId = ledgerId; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("UPDATE tool_call_ledger", StringComparison.Ordinal) || !Carries(command, _ledgerId) || Interlocked.Exchange(ref _fired, 1) == 1) return ValueTask.FromResult(result);

            throw new TimeoutException($"the write to decision {_ledgerId} timed out");
        }
    }

    /// <summary>Fails the save of one card's timed-out mirror, once.</summary>
    private sealed class FailCardSaveOf : DbCommandInterceptor
    {
        private readonly Guid _messageId;
        private int _fired;

        public FailCardSaveOf(Guid messageId) { _messageId = messageId; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        private void Fail(DbCommand command)
        {
            if (command.CommandText.Contains("UPDATE message", StringComparison.Ordinal) && Carries(command, _messageId) && Interlocked.Exchange(ref _fired, 1) == 0)
                throw new TimeoutException($"saving card {_messageId} timed out");
        }
    }

    private static bool Carries(DbCommand command, Guid value) => command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == value);

    private async Task<InteractionState> CardStateAsync(Guid messageId)
    {
        using var scope = _fixture.BeginScope();
        var json = await scope.Resolve<CodeSpaceDbContext>().Message.AsNoTracking().Where(m => m.Id == messageId).Select(m => m.InteractionJson).SingleAsync();

        return MessageInteractionJson.Deserialize(json)!.State;
    }

    private async Task<IReadOnlyList<TimedOutDecision>> ReapAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IToolCallLedgerService>().ExpireStaleDecisionsAsync(DateTimeOffset.UtcNow, CancellationToken.None).ToListAsync();
    }

    private async Task<ToolCallLedger> ReadRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    // ─── Seeding ──────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedDecisionAsync(Guid teamId, DateTimeOffset deadlineAt, string? defaultAction, string policy = DecisionPolicies.SupervisorFirst, Guid? approvalMessageId = null)
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
            ApprovalMessageId = approvalMessageId,
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

    private async Task<Guid> SeedTeamAsync() => (await SeedTeamWithOwnerAsync()).TeamId;

    private async Task<(Guid TeamId, Guid OwnerId)> SeedTeamWithOwnerAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"reaper-{userId:N}@test.local", Name = $"reaper-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"reaper-{teamId:N}", Name = "Decision Reaper Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }

    /// <summary>An open decision card posted by the bot into a fresh channel of the team — the message the sweep mirrors a timed-out decision onto.</summary>
    private async Task<Guid> SeedDecisionCardAsync(Guid teamId, Guid ownerId)
    {
        using var scope = _fixture.BeginScope();
        var slug = "reaper-" + Guid.NewGuid().ToString("N")[..8];
        var channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, default);

        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent { Buttons = new List<InteractionButton> { new() { Key = "a", Label = "A" } } },
            Target = new DecisionRequestTarget { Token = Guid.NewGuid().ToString("N") },
            AllowedResponderUserIds = new[] { ownerId },
        };

        return (await scope.Resolve<IChatBotService>().PostAsBotAsync(channelId, "which migration path?", card, default)).Id;
    }
}
