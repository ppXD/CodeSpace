using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (high fidelity, Rule 12): the D3 reaper ORCHESTRATION driven through the REAL
/// <see cref="ToolApprovalExpiryService"/> + REAL <see cref="ToolCallLedgerService"/> + REAL
/// <see cref="MessageInteractionService"/> + the REAL singleton <see cref="ToolApprovalWaiterRegistry"/> over real
/// Postgres. A real approval card is parked through the REAL <see cref="McpRequestHandler"/> (so the row + the posted
/// card + its stamped message id are all genuine), then with no decision the reaper expires it: the row flips to
/// Expired, the card is mirrored to timed-out (the MessageInteraction resolves), and a same-pod blocked waiter is woken
/// with Expired. The end-to-end then proves a re-call replays the expired terminal WITHOUT posting a second card.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ToolApprovalExpiryServiceTests
{
    private readonly PostgresFixture _fixture;

    public ToolApprovalExpiryServiceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task ExpireDueAsync_expires_the_row_mirrors_the_card_and_signals_a_registered_waiter()
    {
        var (teamId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        var (ledgerId, messageId) = await ParkApprovalAsync(teamId, runId, channelId);

        using var scope = _fixture.BeginScope();

        // A blocked handler on THIS pod registers a waiter — the reaper's best-effort signal must wake it with Expired.
        var waiter = scope.Resolve<IToolApprovalWaiterRegistry>().Register(ledgerId);

        var expired = await scope.Resolve<IToolApprovalExpiryService>().ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        expired.ShouldBeGreaterThanOrEqualTo(1, "the over-deadline undecided row is durably expired");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Expired, "the ledger row is durably Expired (the authority)");

        (await ReadInteractionStateAsync(messageId)).ShouldBe(InteractionState.Resolved, "the approval card is mirrored to timed-out (idempotent)");

        (await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(ToolApprovalOutcome.Expired,
            customMessage: "the same-pod blocked waiter is woken with Expired — check IToolApprovalWaiterRegistry.TrySignal in ToolApprovalExpiryService");
    }

    [Fact]
    public async Task Park_then_no_decision_then_reaper_then_recall_replays_the_expired_terminal_with_no_second_card()
    {
        var (teamId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        var (ledgerId, _) = await ParkApprovalAsync(teamId, runId, channelId);

        // No human decides → the reaper expires the parked row.
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IToolApprovalExpiryService>().ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);

        // The model re-issues the EXACT same call → TryClaim hits the unique index → Duplicate → replays the Expired
        // terminal. It must NOT re-open a new approval (§6.9 no-infinite-loop) and must NOT post a second card.
        var tool = new CountingWriteTool();
        using var recallScope = _fixture.BeginScope();
        var reCall = await CallToolAsync(Handler(recallScope, teamId, runId, channelId, tool), "git.open_pr", new { branch = "main" });

        reCall.GetProperty("isError").GetBoolean().ShouldBeTrue("the re-call replays the expired terminal — a clean error, not a re-open");
        Text(reCall).ShouldContain("expired", customMessage: "the model gets the durable expiry reason on the re-call");
        tool.CallCount.ShouldBe(0, "an expired call never runs the side effect");

        (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(1, "exactly one approval card across the park + expiry + re-call — the re-call never posts a second");
    }

    [Fact]
    public async Task Mediator_dispatch_commits_the_expired_CAS_BEFORE_it_wakes_a_same_pod_waiter()
    {
        var (teamId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        var (ledgerId, _) = await ParkApprovalAsync(teamId, runId, channelId);

        // Drive expiry through the REAL mediator (NOT ExpireDueAsync directly) so TransactionalBehavior wraps the whole
        // command in one transaction — the production path. The signal must fire only AFTER that transaction commits, or
        // a same-pod handler woken inside the transaction re-reads on its OWN connection and sees the row still
        // AwaitingApproval (the lost-fast-path race this fix closes). The recorder snapshots the row's COMMITTED status
        // on a fresh connection at the instant of the wake — exactly what a woken handler observes.
        var recorder = new CommittedStateRecordingWaiterRegistry(_fixture.ConnectionString);

        using var scope = _fixture.BeginScope(b => b.RegisterInstance(recorder).As<IToolApprovalWaiterRegistry>().SingleInstance());

        recorder.Register(ledgerId);   // a same-pod handler is blocked on THIS row

        var result = await scope.Resolve<IMediator>().Send(new ExpireStaleToolApprovalsCommand(), CancellationToken.None);

        result.Expired.ShouldBeGreaterThanOrEqualTo(1, "the over-deadline undecided row is durably expired");

        recorder.SignalledLedgerIds.ShouldContain(ledgerId, "the same-pod waiter for the expired row was woken");

        recorder.CommittedStatusAtSignal(ledgerId).ShouldBe(nameof(ToolCallLedgerStatus.Expired),
            customMessage: "the durable CAS must be COMMITTED before the wake — a fresh-connection read at signal time saw the row as Expired, not the pre-commit AwaitingApproval. If this fails, the signal is firing inside the command transaction (see ExpireDueAsync deferring via IPostCommitActions).");
    }

    [Theory]
    [InlineData(false)]   // the service called with no transaction open: each row's follow-ups run inline, as the sweep reaches it
    [InlineData(true)]    // the recurring job's command: one transaction, the follow-ups drained once it commits
    public async Task A_card_mirror_that_fails_to_save_leaves_the_other_expired_approvals_woken_and_mirrored(bool throughTheCommand)
    {
        // The sweep expires its whole batch, then wakes each row's blocked call and mirrors its card. With no drain to
        // swallow it, one card's failed save used to end those follow-ups right there: every later row unwoken, its card
        // Open, and no tick to come back for it, since the sweep selects only rows still awaiting approval. The drain
        // contains that per row but cannot name the row. And a card whose save failed must not stay tracked: the next
        // card's save would write it as if it had landed.
        var (teamId, channelId) = await SeedTeamChannelAsync();
        var first = await ParkOverdueApprovalAsync(teamId, channelId, LongAgo);
        var faulted = await ParkOverdueApprovalAsync(teamId, channelId, LongAgo.AddSeconds(1));
        var last = await ParkOverdueApprovalAsync(teamId, channelId, LongAgo.AddSeconds(2));

        var log = new RecordingLogger<ToolApprovalExpiryService>();
        using var scope = BeginScopeFailingCardSaveOf(faulted.MessageId, log);
        var waiters = scope.Resolve<IToolApprovalWaiterRegistry>();
        var firstCall = waiters.Register(first.LedgerId);
        var faultedCall = waiters.Register(faulted.LedgerId);
        var lastCall = waiters.Register(last.LedgerId);

        try
        {
            var expired = 0;
            (await Record.ExceptionAsync(async () => expired = await SweepAsync(scope, throughTheCommand))).ShouldBeNull("one card's failed mirror is that card's, not the sweep's");

            // >= not == : the tally is deployment-wide; the rows this test owns are the proof.
            expired.ShouldBeGreaterThanOrEqualTo(3, "all three approvals were durably expired, whatever their follow-ups did");

            foreach (var approval in new[] { first, faulted, last })
                (await ReadRowAsync(approval.LedgerId)).Status.ShouldBe(ToolCallLedgerStatus.Expired, "the ledger row is the authority, and it stands");

            (await WokenAsync(firstCall, "the first approval's blocked call")).ShouldBe(ToolApprovalOutcome.Expired);
            (await WokenAsync(faultedCall, "the blocked call of the approval whose card failed to mirror")).ShouldBe(ToolApprovalOutcome.Expired, customMessage: "the wake is its own step: a failed mirror costs the call nothing");
            (await WokenAsync(lastCall, "the last approval's blocked call, behind the failed card")).ShouldBe(ToolApprovalOutcome.Expired, customMessage: "check ToolApprovalExpiryService reached the rows after the failed mirror");

            (await ReadInteractionStateAsync(first.MessageId)).ShouldBe(InteractionState.Resolved, "the card before the failed one is mirrored");
            (await ReadInteractionStateAsync(last.MessageId)).ShouldBe(InteractionState.Resolved, "and the card after it is mirrored too");
            (await ReadInteractionStateAsync(faulted.MessageId)).ShouldBe(InteractionState.Open, "only the failed card's display mirror was lost — a late click on it is refused, its row being Expired — and no later save wrote it");

            log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ShouldContain(m => m.Contains(faulted.LedgerId.ToString()), "the failed mirror is logged against its own ledger row");
            scope.Resolve<CodeSpaceDbContext>().ChangeTracker.HasChanges().ShouldBeFalse("a card whose save failed is forgotten, not left tracked for the next save");
        }
        finally
        {
            waiters.Remove(first.LedgerId);
            waiters.Remove(faulted.LedgerId);
            waiters.Remove(last.LedgerId);
        }
    }

    // ─── Park a real approval card through the real handler (times out fast → row stays AwaitingApproval, then back-date the deadline) ───

    private async Task<(Guid LedgerId, Guid MessageId)> ParkApprovalAsync(Guid teamId, Guid runId, Guid channelId)
    {
        var previous = Environment.GetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar);
        Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, "1");   // tiny bound → park without a human

        try
        {
            using var scope = _fixture.BeginScope();
            await CallToolAsync(Handler(scope, teamId, runId, channelId, new CountingWriteTool()), "git.open_pr", new { branch = "main" });
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, previous);
        }

        var row = (await ReadRunRowsAsync(teamId, runId)).ShouldHaveSingleItem();
        row.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the parked call left the row AwaitingApproval");
        row.ApprovalMessageId.ShouldNotBeNull("the parked call stamped the posted card's message id");

        // Back-date the deadline so the reaper (running at real now) treats it as already past-due.
        await BackdateDeadlineAsync(row.Id);

        return (row.Id, row.ApprovalMessageId!.Value);
    }

    private async Task BackdateDeadlineAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger
            .Where(l => l.Id == ledgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ApprovalDeadlineAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    private McpRequestHandler Handler(ILifetimeScope scope, Guid teamId, Guid runId, Guid channelId, IAgentTool tool) =>
        new(new SingleToolRegistry(tool), AgentAutonomyLevel.Standard, teamId, null, runId,
            scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true, approvalConversationId: channelId,
            scope.Resolve<IChatBotService>(), scope.Resolve<IToolApprovalWaiterRegistry>(), scope.Resolve<IInteractionComponentRegistry>());

    /// <summary>Overdue ahead of anything else overdue in this shared database, so the sweep reaches these rows first.</summary>
    private static readonly DateTimeOffset LongAgo = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>An undecided approval exactly as a parked call leaves it — claimed, parked with its token and deadline, its card posted and recorded on the row — without the handler's bounded wait.</summary>
    private async Task<(Guid LedgerId, Guid MessageId)> ParkOverdueApprovalAsync(Guid teamId, Guid channelId, DateTimeOffset deadlineAt)
    {
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IToolCallLedgerService>();
        var token = Guid.NewGuid().ToString("N");

        var claim = await ledger.TryClaimAsync(Guid.NewGuid(), teamId, "git.open_pr", Guid.NewGuid().ToString("N"), "input-hash", 0, CancellationToken.None);
        (await ledger.TryBeginApprovalAsync(claim.LedgerId, teamId, new ToolCallApprovalPark { Token = token, DeadlineAt = deadlineAt }, CancellationToken.None)).ShouldBeTrue("fixture check: the claimed row parks for approval");

        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent
            {
                Buttons = new List<InteractionButton>
                {
                    new() { Key = "approve", Label = "Approve", Style = InteractionButtonStyle.Primary },                          // McpRequestHandler.ApprovalButtonsConfig
                    new() { Key = "reject", Label = "Reject", Style = InteractionButtonStyle.Danger, RequiresComment = true },
                },
            },
            Target = new ToolCallApprovalTarget { Token = token },
            AllowedResponderUserIds = null,
            Resolve = new ResolvePolicy(),
        };

        var posted = await scope.Resolve<IChatBotService>().PostAsBotAsync(channelId, "Agent run requests approval to run **git.open_pr**. Approve to let it proceed, or reject to refuse it.", card, CancellationToken.None);
        await ledger.SetApprovalMessageAsync(claim.LedgerId, teamId, posted.Id, CancellationToken.None);

        return (claim.LedgerId, posted.Id);
    }

    /// <summary>A scope whose database commands pass the interceptor that fails one card's mirror, and whose expiry service logs to <paramref name="log"/>.</summary>
    private ILifetimeScope BeginScopeFailingCardSaveOf(Guid messageId, ILogger<ToolApprovalExpiryService> log)
    {
        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(new FailCardSaveOf(messageId)).Options;

        return _fixture.BeginScope(b =>
        {
            b.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
            b.RegisterInstance<ILogger<ToolApprovalExpiryService>>(log);
        });
    }

    /// <summary>One sweep: the service called straight, with no transaction open, or the command the recurring job sends, through the mediator's own transaction and post-commit drain.</summary>
    private static async Task<int> SweepAsync(ILifetimeScope scope, bool throughTheCommand) =>
        throughTheCommand
            ? (await scope.Resolve<IMediator>().Send(new ExpireStaleToolApprovalsCommand(), CancellationToken.None)).Expired
            : await scope.Resolve<IToolApprovalExpiryService>().ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

    /// <summary>The outcome <paramref name="signal"/> was woken with; fails by its name when it is not woken within five seconds.</summary>
    private static async Task<ToolApprovalOutcome> WokenAsync(IToolApprovalWaiter call, string signal)
    {
        var first = await Task.WhenAny(call.Completion, Task.Delay(TimeSpan.FromSeconds(5)));

        (first == call.Completion).ShouldBeTrue($"{signal} was not woken within 5s — check ToolApprovalExpiryService.ResolveAsync reached it");

        return await call.Completion;
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

        private static bool Carries(DbCommand command, Guid value) => command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == value);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    // ─── Reads ───────────────────────────────────────────────────────────────────

    private async Task<ToolCallLedger> ReadRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    private async Task<IReadOnlyList<ToolCallLedger>> ReadRunRowsAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None);
    }

    private async Task<int> ReadRunCardCountAsync(Guid teamId, Guid channelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Message.AsNoTracking()
            .CountAsync(m => m.ConversationId == channelId && m.TeamId == teamId && m.InteractionJson != null && m.DeletedDate == null);
    }

    private async Task<InteractionState> ReadInteractionStateAsync(Guid messageId)
    {
        using var scope = _fixture.BeginScope();
        var json = (await scope.Resolve<CodeSpaceDbContext>().Message.AsNoTracking().SingleAsync(m => m.Id == messageId)).InteractionJson;
        return MessageInteractionJson.Deserialize(json)!.State;
    }

    private static async Task<JsonElement> CallToolAsync(McpRequestHandler handler, string name, object arguments)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
        var resp = (await handler.HandleAsync(JsonDocument.Parse(request).RootElement.Clone(), CancellationToken.None))!.Value;
        return resp.GetProperty("result");
    }

    private static string Text(JsonElement toolResult) => toolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    // ─── Seeding ─────────────────────────────────────────────────────────────────

    private async Task<(Guid TeamId, Guid ChannelId)> SeedTeamChannelAsync()
    {
        Guid teamId, ownerId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            ownerId = Guid.NewGuid();
            db.User.Add(new User { Id = ownerId, Email = $"exp-{ownerId:N}@test.local", Name = $"exp-{ownerId:N}" });

            teamId = Guid.NewGuid();
            db.Team.Add(new Team { Id = teamId, Slug = $"exp-{teamId:N}", Name = "Expiry Team", Kind = TeamKind.Workspace });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = ownerId, Role = TeamRole.Owner });

            await db.SaveChangesAsync();
        }

        using var s2 = _fixture.BeginScope();
        var slug = "exp-" + Guid.NewGuid().ToString("N")[..8];
        var channelId = await s2.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, CancellationToken.None);

        return (teamId, channelId);
    }

    /// <summary>A side-effecting tool that counts its invocations — the expired-call proof asserts the count stays 0.</summary>
    private sealed class CountingWriteTool : IAgentTool
    {
        public int CallCount { get; private set; }
        public string Kind => "git.open_pr";
        public string Description => "open a PR";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public JsonElement OutputSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        public bool IsReadOnly => false;
        public bool IsDestructive => true;

        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;

        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(AgentToolResult.Ok(JsonDocument.Parse("""{"opened":true}""").RootElement.Clone(), 14));
        }
    }

    private sealed class SingleToolRegistry : IAgentToolRegistry
    {
        private readonly IAgentTool _tool;
        public SingleToolRegistry(IAgentTool tool) => _tool = tool;
        public IReadOnlyList<IAgentTool> All => new[] { _tool };
        public IAgentTool? Resolve(string kind) => kind == _tool.Kind ? _tool : null;
    }

    /// <summary>
    /// A waiter registry that, at the instant the reaper signals a row, reads that row's COMMITTED status on a FRESH
    /// connection — exactly what a same-pod blocked handler does when it wakes and re-reads on its own scope. If the
    /// signal fired inside the still-open command transaction, this fresh connection (which sees only committed data)
    /// reads the row as AwaitingApproval; once the CAS is committed-before-wake, it reads Expired. The whole-call
    /// surface stays a real in-memory waiter map so the production wake path is genuinely exercised.
    /// </summary>
    private sealed class CommittedStateRecordingWaiterRegistry : IToolApprovalWaiterRegistry
    {
        private readonly string _connectionString;
        private readonly ToolApprovalWaiterRegistry _inner = new();
        private readonly Dictionary<Guid, string?> _committedStatusAtSignal = new();

        public CommittedStateRecordingWaiterRegistry(string connectionString) => _connectionString = connectionString;

        public List<Guid> SignalledLedgerIds { get; } = new();

        public string? CommittedStatusAtSignal(Guid ledgerId) => _committedStatusAtSignal.GetValueOrDefault(ledgerId);

        public IToolApprovalWaiter Register(Guid ledgerId) => _inner.Register(ledgerId);

        public bool TrySignal(Guid ledgerId, ToolApprovalOutcome outcome)
        {
            SignalledLedgerIds.Add(ledgerId);
            _committedStatusAtSignal[ledgerId] = ReadCommittedStatus(ledgerId);

            return _inner.TrySignal(ledgerId, outcome);
        }

        public void Remove(Guid ledgerId) => _inner.Remove(ledgerId);

        private string? ReadCommittedStatus(Guid ledgerId)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();

            using var cmd = new NpgsqlCommand("SELECT status FROM tool_call_ledger WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", ledgerId);

            return cmd.ExecuteScalar() as string;
        }
    }
}
