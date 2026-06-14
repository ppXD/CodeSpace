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
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
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
            db.Team.Add(new Team { Id = teamId, Slug = $"exp-{teamId:N}", Name = "Expiry Team", Kind = TeamKind.Workspace, OwnerUserId = ownerId });
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
}
