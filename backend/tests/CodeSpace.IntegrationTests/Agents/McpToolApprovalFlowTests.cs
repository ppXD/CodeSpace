using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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
/// 🟢 Integration (high fidelity, Rule 12): the durable mid-turn HITL bounded-block approval loop driven through the
/// REAL <see cref="McpRequestHandler"/> + REAL <see cref="ToolCallLedgerService"/> + REAL <see cref="ChatBotService"/>
/// + REAL <see cref="MessageInteractionService"/> respond path (which routes a <see cref="ToolCallApprovalTarget"/> to
/// the REAL <see cref="ToolCallApprovalResolver"/>) + the REAL singleton <see cref="ToolApprovalWaiterRegistry"/> over
/// real Postgres. A side-effecting tool call at Standard/Trusted records an AwaitingApproval row, posts an approval
/// card, and BLOCKS the synchronous tools/call (run on a background task) until a human responds through the same
/// chat respond path a real reviewer's click drives. Covers: approve → side effect runs ONCE → real result → Succeeded
/// + card Resolved; reject → Failed refusal → card Resolved → not run; bound-timeout (env var set tiny) → pending-ticket
/// → row stays AwaitingApproval → a later approve + re-call runs once; Confined → flat Deny, no card; Unleashed →
/// straight execute, no card; NO approval conversation → flat refusal, no card, no block (the conversation-less-run
/// safety); only ONE card per (run, key) on a re-call; two concurrent approve responders → exactly one execution.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class McpToolApprovalFlowTests
{
    private readonly PostgresFixture _fixture;

    public McpToolApprovalFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted)]
    public async Task Approve_runs_the_side_effect_once_returns_the_real_result_and_marks_the_row_and_card_resolved(AgentAutonomyLevel level)
    {
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = ApprovalHandler(scope, level, teamId, runId, channelId, tool);

        // tools/call BLOCKS until the decision lands → run it on a background task.
        var call = Task.Run(() => CallToolAsync(handler, "git.open_pr", new { title = "Fix", branch = "main" }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        await RespondAsync(teamId, messageId, ApproveKey, ownerId);

        var result = await call;   // the blocked call wakes, runs the side effect, returns the real result

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("the approved call returns the REAL (slow) tool result");
        Text(result).ShouldContain("opened", customMessage: "the model gets the actual tool output, not a refusal");
        tool.CallCount.ShouldBe(1, "the side effect runs EXACTLY once — after approval");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the AwaitingApproval row flips to Succeeded via the terminal CAS");
        row.ApprovedAt.ShouldNotBeNull();

        (await ReadInteractionStateAsync(messageId)).ShouldBe(InteractionState.Resolved, "the approval card is stamped resolved");
    }

    [Fact]
    public async Task Reject_returns_the_refusal_does_not_run_the_side_effect_and_marks_the_row_Failed_and_card_resolved()
    {
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = ApprovalHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool);

        var call = Task.Run(() => CallToolAsync(handler, "git.open_pr", new { branch = "main" }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        await RespondAsync(teamId, messageId, RejectKey, ownerId, comment: "no thanks");

        var result = await call;

        result.GetProperty("isError").GetBoolean().ShouldBeTrue("a rejected call returns an isError refusal");
        tool.CallCount.ShouldBe(0, "a rejected call NEVER runs the side effect");

        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Failed, "reject drives AwaitingApproval → Failed");
        (await ReadInteractionStateAsync(messageId)).ShouldBe(InteractionState.Resolved, "the card is stamped resolved by the reject");
    }

    [Fact]
    public async Task A_bound_timeout_returns_the_pending_ticket_leaves_the_row_awaiting_then_a_later_approve_and_recall_runs_once()
    {
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        var previous = Environment.GetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar);
        Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, "1");   // tiny bound → time out without a human

        try
        {
            using var scope = _fixture.BeginScope();
            var handler = ApprovalHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool);

            // First call: no one responds within the 1s bound → the pending-ticket, row stays AwaitingApproval.
            var first = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

            first.GetProperty("isError").GetBoolean().ShouldBeTrue("a bound-elapsed call returns the typed pending-ticket");
            Text(first).ShouldContain("Awaiting human approval", customMessage: "the pending-ticket names the ledger ticket to retry");
            tool.CallCount.ShouldBe(0, "no decision yet → the side effect has not run");

            var ledgerId = (await ReadRunRowsAsync(teamId, runId)).ShouldHaveSingleItem().Id;
            (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the row stays AwaitingApproval for a later decision — NOT stranded");
            var messageId = (await ReadRowAsync(ledgerId)).ApprovalMessageId!.Value;

            // A human approves out-of-band, THEN the model re-issues the exact call → it runs the side effect once.
            await RespondAsync(teamId, messageId, ApproveKey, ownerId);

            var reCall = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

            reCall.GetProperty("isError").GetBoolean().ShouldBeFalse("the re-call after approval runs and returns the real result");
            tool.CallCount.ShouldBe(1, "exactly once across the timed-out first call + the approved re-call");
            (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded);

            // Only ONE card was ever posted for the (run, key) — the re-call must not post a second.
            (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(1, "exactly one approval card per (run, key) across the timeout + re-call");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, previous);
        }
    }

    [Fact]
    public async Task A_recall_while_awaiting_does_not_post_a_second_card()
    {
        var (teamId, _, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        var previous = Environment.GetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar);
        Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, "1");

        try
        {
            using var scope = _fixture.BeginScope();
            var handler = ApprovalHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool);

            await CallToolAsync(handler, "git.open_pr", new { branch = "main" });   // posts the card, times out → pending-ticket
            await CallToolAsync(handler, "git.open_pr", new { branch = "main" });   // re-call while still AwaitingApproval, no decision → ticket again

            (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(1, "a re-call of a still-parked (run, key) must NOT post a second card");
            tool.CallCount.ShouldBe(0, "still no decision → the side effect never ran");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, previous);
        }
    }

    [Fact]
    public async Task Confined_flat_denies_with_no_card_and_no_block()
    {
        var (teamId, _, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = ApprovalHandler(scope, AgentAutonomyLevel.Confined, teamId, runId, channelId, tool);

        var result = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(result).ShouldContain("not permitted", customMessage: "Confined is a flat Deny — never approvable");
        tool.CallCount.ShouldBe(0);
        (await ReadRunRowsAsync(teamId, runId)).ShouldBeEmpty("a Deny writes no ledger row");
        (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(0, "a Deny posts no approval card");
    }

    [Fact]
    public async Task Unleashed_executes_straight_through_with_no_card()
    {
        var (teamId, _, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = ApprovalHandler(scope, AgentAutonomyLevel.Unleashed, teamId, runId, channelId, tool);

        var result = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("Unleashed runs the gated tool unattended");
        tool.CallCount.ShouldBe(1);
        (await ReadRunRowsAsync(teamId, runId)).ShouldHaveSingleItem().Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the Unleashed Allow path records a terminal directly — no AwaitingApproval");
        (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(0, "the Allow path posts no card");
    }

    [Fact]
    public async Task A_run_with_no_approval_conversation_flat_refuses_with_no_card_and_no_block_the_safety()
    {
        var (teamId, _, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        // Governance ON + every collaborator wired, but NO approval conversation → fail-closed to the flat refusal.
        var handler = new McpRequestHandler(new SingleToolRegistry(tool), AgentAutonomyLevel.Standard, teamId, null, runId,
            scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true, approvalConversationId: null,
            scope.Resolve<IChatBotService>(), scope.Resolve<IToolApprovalWaiterRegistry>(), scope.Resolve<IInteractionComponentRegistry>());

        var result = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(result).ShouldContain("approval", customMessage: "no approval surface → the pre-D2 flat refusal");
        tool.CallCount.ShouldBe(0, "the conversation-less run never blocks and never runs the tool");
        (await ReadRunRowsAsync(teamId, runId)).ShouldBeEmpty("no approval surface → no ledger row, byte-identical to today");
        (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(0, "no card posted on a conversation-less run");
    }

    [Fact]
    public async Task Two_concurrent_approve_responders_yield_exactly_one_execution()
    {
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var member2 = await SeedConversationMemberAsync(teamId, ownerId, channelId);
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = ApprovalHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool);

        var call = Task.Run(() => CallToolAsync(handler, "git.open_pr", new { branch = "main" }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        // Two members click Approve at the same instant. The FOR UPDATE lock in the respond path + the
        // approved_at == null CAS in the resolver serialize them → exactly one stamps the decision.
        var outcomes = await Task.WhenAll(
            TryRespondAsync(teamId, messageId, ApproveKey, ownerId),
            TryRespondAsync(teamId, messageId, ApproveKey, member2));

        var result = await call;

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        outcomes.Count(ok => ok).ShouldBe(1, "exactly one approve wins; the loser is rejected as already-resolved");
        tool.CallCount.ShouldBe(1, "the AwaitingApproval → terminal single-winner CAS guarantees exactly one execution");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
    }

    // ── The REAL endpoint over a per-run UDS (Tier 🟢 — proves the endpoint threads the D2 deps + blocks over the socket) ──

    [Fact]
    public async Task Over_the_real_UDS_endpoint_an_approval_card_blocks_the_call_then_a_human_approve_returns_the_real_result()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!System.Net.Sockets.Socket.OSSupportsUnixDomainSockets) return;

        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        var socketPath = Path.Combine(Path.GetTempPath(), $"cs-approval-{Guid.NewGuid():N}.sock");
        var token = $"tok-{Guid.NewGuid():N}";

        await using var endpoint = NewEndpoint(runId, teamId, channelId, socketPath, token, tool);

        await using var client = await UdsClient.ConnectAsync(socketPath, token);

        // tools/call over the socket BLOCKS in the endpoint's handler until the human decides → drive it on a task.
        var call = Task.Run(() => client.CallToolAsync(1, "git.open_pr", new { branch = "main" }));

        var (_, messageId) = await WaitForPostedCardAsync(teamId, runId);

        await RespondAsync(teamId, messageId, ApproveKey, ownerId);

        var result = await call;

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("the approved call returns the real result back over the UDS");
        Text(result).ShouldContain("opened");
        tool.CallCount.ShouldBe(1, "exactly one execution, end-to-end through the real endpoint + handler + respond path");
    }

    // ─── Build the handler / endpoint with the full approval surface ─────────────

    private McpRequestHandler ApprovalHandler(ILifetimeScope scope, AgentAutonomyLevel autonomy, Guid teamId, Guid runId, Guid channelId, IAgentTool tool) =>
        new(new SingleToolRegistry(tool), autonomy, teamId, null, runId,
            scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true, approvalConversationId: channelId,
            scope.Resolve<IChatBotService>(), scope.Resolve<IToolApprovalWaiterRegistry>(), scope.Resolve<IInteractionComponentRegistry>());

    private AgentMcpEndpoint NewEndpoint(Guid runId, Guid teamId, Guid channelId, string socketPath, string token, IAgentTool tool)
    {
        // A dedicated DI scope the endpoint owns + disposes (mirrors AgentRunExecutor.OpenMcpEndpointIfEnabled). The
        // connect registry is the shared singleton; the per-connection handler scope is minted off this one.
        var scope = _fixture.BeginScope();
        var dedicated = scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>().CreateScope();
        return new AgentMcpEndpoint(runId, new SingleToolRegistry(tool), AgentAutonomyLevel.Standard, teamId,
            SecretRedactor.None, socketPath, token, scope.Resolve<IAgentMcpConnectRegistry>(), dedicated,
            CancellationToken.None, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, 0, governanceEnabled: true, approvalConversationId: channelId);
    }

    // ─── Drive the real respond path ─────────────────────────────────────────────

    private async Task RespondAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId, string? comment = null)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMessageInteractionService>().RespondAsync(teamId, messageId, responseKey, actorUserId, comment, null, CancellationToken.None);
    }

    private async Task<bool> TryRespondAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId)
    {
        try { await RespondAsync(teamId, messageId, responseKey, actorUserId); return true; }
        catch (InvalidOperationException) { return false; }   // the loser of a concurrent race — already resolved
    }

    // ─── Poll for the posted card (the blocked call posts it asynchronously) ──────

    private async Task<(Guid LedgerId, Guid MessageId)> WaitForPostedCardAsync(Guid teamId, Guid runId)
    {
        for (var i = 0; i < 200; i++)   // ~10s budget under full-suite Postgres contention
        {
            using (var scope = _fixture.BeginScope())
            {
                var row = await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking()
                    .Where(l => l.AgentRunId == runId && l.TeamId == teamId && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovalMessageId != null)
                    .Select(l => new { l.Id, l.ApprovalMessageId })
                    .FirstOrDefaultAsync();

                if (row is not null) return (row.Id, row.ApprovalMessageId!.Value);
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The approval card for run {runId} was not posted within 10s — the blocked tools/call should record AwaitingApproval + stamp the message id before parking.");
    }

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

    private async Task<(Guid TeamId, Guid OwnerId, Guid ChannelId)> SeedTeamChannelAsync()
    {
        Guid teamId, ownerId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            ownerId = Guid.NewGuid();
            db.User.Add(new User { Id = ownerId, Email = $"appr-{ownerId:N}@test.local", Name = $"appr-{ownerId:N}" });

            teamId = Guid.NewGuid();
            db.Team.Add(new Team { Id = teamId, Slug = $"appr-{teamId:N}", Name = "Approval Team", Kind = TeamKind.Workspace, OwnerUserId = ownerId });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = ownerId, Role = TeamRole.Owner });

            await db.SaveChangesAsync();
        }

        using var s2 = _fixture.BeginScope();
        var slug = "appr-" + Guid.NewGuid().ToString("N")[..8];
        var channelId = await s2.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, CancellationToken.None);

        return (teamId, ownerId, channelId);
    }

    private async Task<Guid> SeedConversationMemberAsync(Guid teamId, Guid ownerId, Guid channelId)
    {
        Guid memberId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            memberId = Guid.NewGuid();
            db.User.Add(new User { Id = memberId, Email = $"m2-{memberId:N}@test.local", Name = $"m2-{memberId:N}" });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = memberId, Role = TeamRole.Member });
            await db.SaveChangesAsync();
        }

        using var s2 = _fixture.BeginScope();
        await s2.Resolve<IConversationService>().AddMemberAsync(teamId, ownerId, channelId, memberId, CancellationToken.None);
        return memberId;
    }

    private const string ApproveKey = "approve";
    private const string RejectKey = "reject";

    /// <summary>A side-effecting (destructive → RequireApproval at Standard/Trusted) tool that counts its invocations — the exactly-once proof asserts the count.</summary>
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

    /// <summary>A real AF_UNIX client (the proxy's stand-in) — sends the run token as line 1, then newline-delimited JSON-RPC (mirrors AgentMcpEndpointFlowTests.McpClient).</summary>
    private sealed class UdsClient : IAsyncDisposable
    {
        private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly System.Net.Sockets.Socket _socket;
        private readonly System.Net.Sockets.NetworkStream _net;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private UdsClient(System.Net.Sockets.Socket socket)
        {
            _socket = socket;
            _net = new System.Net.Sockets.NetworkStream(socket, ownsSocket: false);
            _reader = new StreamReader(_net, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
            _writer = new StreamWriter(_net, Utf8NoBom) { AutoFlush = false, NewLine = "\n" };
        }

        public static async Task<UdsClient> ConnectAsync(string socketPath, string token)
        {
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);

            for (var i = 0; i < 100 && !File.Exists(socketPath); i++) await Task.Delay(50);   // the endpoint binds asynchronously in its ctor's accept loop setup
            await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));

            var client = new UdsClient(socket);
            await client._writer.WriteLineAsync(token);
            await client._writer.FlushAsync();
            return client;
        }

        public async Task<JsonElement> CallToolAsync(int id, string name, object arguments)
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } }));
            await _writer.FlushAsync();

            var line = await _reader.ReadLineAsync() ?? throw new InvalidOperationException("endpoint closed before a response");
            return JsonDocument.Parse(line).RootElement.Clone().GetProperty("result");
        }

        public async ValueTask DisposeAsync()
        {
            try { _writer.Dispose(); } catch { /* broken pipe on flush */ }
            _reader.Dispose();
            await _net.DisposeAsync();
            _socket.Dispose();
        }
    }
}
