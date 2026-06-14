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
    public async Task An_always_approve_merge_at_Unleashed_posts_a_card_and_blocks_instead_of_auto_running()
    {
        // THE E2 finale: git.merge_pr is irreversible (AlwaysRequiresApproval) → even at the most permissive
        // Unleashed tier it must NOT auto-run. The gate escalates Allow → RequireApproval, so the handler posts the
        // D2 approval card and BLOCKS — exactly the path a reversible write takes only at Standard/Trusted. A human
        // approve then runs the merge exactly once. This is the difference from the F writes (Unleashed → straight
        // execute, no card).
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool(alwaysApprove: true);   // Kind == "git.merge_pr"

        using var scope = _fixture.BeginScope();
        var handler = ApprovalHandler(scope, AgentAutonomyLevel.Unleashed, teamId, runId, channelId, tool);

        // The merge BLOCKS at Unleashed (unlike a reversible write) → run it on a background task.
        var call = Task.Run(() => CallToolAsync(handler, "git.merge_pr", new { number = 7 }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        tool.CallCount.ShouldBe(0, "the irreversible merge has NOT run while the card is pending — it never auto-ran at Unleashed");

        await RespondAsync(teamId, messageId, ApproveKey, ownerId);

        var result = await call;

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("the approved merge returns the real result");
        tool.CallCount.ShouldBe(1, "the merge runs EXACTLY once — only after the human approval");

        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the AwaitingApproval row flips to Succeeded after the approved merge");
        (await ReadInteractionStateAsync(messageId)).ShouldBe(InteractionState.Resolved, "the merge approval card is stamped resolved");
        (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(1, "exactly one approval card was posted for the merge at Unleashed");
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
        tool.CallCount.ShouldBe(1, "the AwaitingApproval → Running execution-claim CAS guarantees exactly one execution");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
    }

    [Fact]
    public async Task Two_concurrent_executors_of_the_same_approved_row_run_the_side_effect_exactly_once()
    {
        // The exactly-once-after-approve proof at the EXECUTOR level (not the approver level): the row is already
        // approved, then TWO handler executors race ClaimThenExecuteAsync for the same (run, key). The AwaitingApproval
        // → Running execution-claim CAS runs BEFORE tool.CallAsync, so exactly one executor wins the claim + runs the
        // side effect; the loser re-reads + replays. Pre-fix, both passed the ApprovedAt != null read and both called
        // tool.CallAsync (the side effect ran TWICE). The shared tool counter is the proof.
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        // Park + approve the row up front (one handler posts the card, one human approves), so both racing executors
        // below find an APPROVED AwaitingApproval row and race the execution claim — not the approval decision.
        var previous = Environment.GetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar);
        Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, "1");   // first call times out → row stays approved-AwaitingApproval

        try
        {
            using (var parkScope = _fixture.BeginScope())
            {
                await CallToolAsync(ApprovalHandler(parkScope, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool), "git.open_pr", new { branch = "main" });
            }

            var ledgerId = (await ReadRunRowsAsync(teamId, runId)).ShouldHaveSingleItem().Id;
            var messageId = (await ReadRowAsync(ledgerId)).ApprovalMessageId!.Value;

            await RespondAsync(teamId, messageId, ApproveKey, ownerId);   // human approves → ApprovedAt stamped, row still AwaitingApproval

            // Two executors re-issue the SAME approved call concurrently (each its own scope/DbContext → a real race).
            async Task<JsonElement> ReExecuteAsync()
            {
                using var scope = _fixture.BeginScope();
                return await CallToolAsync(ApprovalHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool), "git.open_pr", new { branch = "main" });
            }

            var results = await Task.WhenAll(ReExecuteAsync(), ReExecuteAsync());

            tool.CallCount.ShouldBe(1, "the execution-claim CAS runs BEFORE the side effect, so exactly one executor runs it once");
            results.Count(r => !r.GetProperty("isError").GetBoolean()).ShouldBeGreaterThanOrEqualTo(1, "at least the winner returns the real result");
            (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the winner's Running → Succeeded terminal lands");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, previous);
        }
    }

    [Fact]
    public async Task A_foreign_team_approval_conversation_flat_refuses_with_no_card_and_no_foreign_bot_membership()
    {
        // Cross-tenant safety: the run's node config names a conversation owned by ANOTHER team. PostAsBotAsync would
        // derive the team FROM that conversation and post the card into (+ auto-join the bot to) the foreign team's
        // chat. The tenancy guard (ConversationBelongsToTeamAsync == _teamId) fail-closes EXACTLY like a conversation-less
        // run: flat refusal, no card, no ledger row, and crucially NO bot membership minted in the foreign conversation.
        var (teamA, _, _) = await SeedTeamChannelAsync();
        var (teamB, _, foreignChannel) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = ApprovalHandler(scope, AgentAutonomyLevel.Standard, teamA, runId, foreignChannel, tool);   // team-A run, team-B conversation

        var result = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(result).ShouldContain("approval", customMessage: "a foreign-team conversation id fail-closes to the flat refusal, byte-identical to the conversation-less run");
        tool.CallCount.ShouldBe(0, "a cross-tenant run never blocks and never runs the tool");
        (await ReadRunRowsAsync(teamA, runId)).ShouldBeEmpty("no approval surface → no ledger row");
        (await ReadRunCardCountAsync(teamB, foreignChannel)).ShouldBe(0, "NO card is posted into the foreign team's conversation");
        (await BotMemberCountAsync(foreignChannel)).ShouldBe(0, "the bot is NEVER force-joined to the foreign team's conversation");
    }

    [Fact]
    public async Task Reattach_mid_approval_approves_through_a_new_instance_and_runs_exactly_once_across_the_boundary()
    {
        // DURABILITY across a WORKER TEARDOWN: a tool call parks an approval on ONE handler instance (its own DI scope
        // = its own DbContext + connection-scoped collaborators). That scope is then DISPOSED — the worker is torn
        // down mid-approval, exactly as a deploy / crash / reconciler reclaim would. A BRAND-NEW handler instance for
        // the SAME run (a fresh scope = a fresh DbContext, the reattached worker) is opened; a human approves via the
        // real respond path, and the model re-issues the exact call THROUGH THE NEW INSTANCE. The durable ledger row
        // is the only thing that survives the boundary, so the side effect runs EXACTLY once + NO second card is
        // posted across the two instances. The review rested this on inference; this pins it.
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        var previous = Environment.GetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar);
        Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, "1");   // first call times out → row parks, then the worker tears down

        Guid ledgerId, messageId;

        try
        {
            // ── Instance 1: park the approval, then DISPOSE the scope (worker teardown mid-approval). ──
            using (var scope1 = _fixture.BeginScope())
            {
                var park = await CallToolAsync(ApprovalHandler(scope1, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool), "git.open_pr", new { branch = "main" });

                park.GetProperty("isError").GetBoolean().ShouldBeTrue("the first call bound-elapses to the pending-ticket while the worker is up");
                tool.CallCount.ShouldBe(0, "no decision yet → the side effect has not run on instance 1");

                ledgerId = (await ReadRunRowsAsync(teamId, runId)).ShouldHaveSingleItem().Id;
                messageId = (await ReadRowAsync(ledgerId)).ApprovalMessageId!.Value;
                (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the durable row stays AwaitingApproval — it must survive the teardown");
            }   // scope1 disposed → instance 1 + its DbContext + connection collaborators are gone

            // ── A human approves out-of-band (its own scope), then instance 2 re-calls THROUGH a fresh instance. ──
            await RespondAsync(teamId, messageId, ApproveKey, ownerId);

            using (var scope2 = _fixture.BeginScope())
            {
                var reCall = await CallToolAsync(ApprovalHandler(scope2, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool), "git.open_pr", new { branch = "main" });

                reCall.GetProperty("isError").GetBoolean().ShouldBeFalse("the re-call on the NEW instance reads the durable approved row + runs the real result");
            }

            tool.CallCount.ShouldBe(1, "exactly once across the instance boundary — the durable ledger, not in-memory state, carries the decision");
            (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
            (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(1, "no SECOND card was posted by the new instance — one card per (run, key) across the reattach");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, previous);
        }
    }

    [Fact]
    public async Task The_reaper_expiring_a_row_wakes_a_genuinely_blocked_call_which_returns_the_Expired_terminal()
    {
        // REAPER vs a LIVE blocked call: a handler is GENUINELY blocked in BlockForDecisionAsync (a Task.Run-d
        // tools/call parked in Task.WhenAny on the waiter, NOT a tiny-bound timeout), while the D3 reaper
        // (IToolApprovalExpiryService.ExpireStaleApprovalsAsync via ExpireDueAsync) durably expires its row. The
        // reaper's same-pod waiter wake must make the blocked call resume IMMEDIATELY, re-read the now-Expired row,
        // and return the Expired terminal (an isError refusal carrying the expiry reason). The side effect never runs.
        var (teamId, _, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        var previous = Environment.GetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar);
        Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, "60");   // a LONG bound → the call genuinely blocks; the reaper, not the timeout, ends the wait

        try
        {
            using var scope = _fixture.BeginScope();
            var handler = ApprovalHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId, tool);

            // The call BLOCKS on the waiter (60s bound) → drive it on a background task; it must NOT return until the reaper fires.
            var call = Task.Run(() => CallToolAsync(handler, "git.open_pr", new { branch = "main" }));

            var (ledgerId, _) = await WaitForPostedCardAsync(teamId, runId);   // the card posted → the handler is now parked in BlockForDecisionAsync

            call.IsCompleted.ShouldBeFalse("the call is genuinely blocked on the waiter — the 60s bound has not elapsed");

            // The reaper expires due rows (now far enough ahead that the deadline is past). Outside a command
            // transaction the post-commit waiter wake runs inline → it signals the live blocked waiter. The reaper is
            // global (team-agnostic), so a sibling test's parked row may also expire — assert THIS row was expired (≥1).
            int expired;
            using (var reaperScope = _fixture.BeginScope())
                expired = await reaperScope.Resolve<IToolApprovalExpiryService>().ExpireDueAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

            expired.ShouldBeGreaterThanOrEqualTo(1, "the reaper durably expired this undecided past-deadline approval (and possibly sibling rows — it's a global sweep)");

            var result = await call;   // the wake makes the blocked call resume + re-read the Expired terminal

            result.GetProperty("isError").GetBoolean().ShouldBeTrue("the woken call returns the Expired terminal as a refusal");
            Text(result).ShouldContain("expired", customMessage: "the blocked call replays the Expired terminal's reason");
            tool.CallCount.ShouldBe(0, "an expired approval NEVER runs the side effect");
            (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Expired, "the durable row is Expired — the reaper CAS is the authority");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, previous);
        }
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

    /// <summary>Count the bot members of a conversation — the cross-tenant guard must NEVER force-join the bot to a foreign conversation. IgnoreQueryFilters so bot users (hidden by the default filter) are visible to the assertion.</summary>
    private async Task<int> BotMemberCountAsync(Guid channelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.ConversationMember.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.ConversationId == channelId && m.DeletedDate == null && db.User.IgnoreQueryFilters().Any(u => u.Id == m.UserId && u.IsBot));
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

    /// <summary>A side-effecting (destructive → RequireApproval at Standard/Trusted) tool that counts its invocations — the exactly-once proof asserts the count. <paramref name="alwaysApprove"/> mirrors git.merge_pr: an irreversible tool that escalates even Unleashed's Allow → RequireApproval, so it can never auto-run.</summary>
    private sealed class CountingWriteTool : IAgentTool
    {
        private readonly bool _alwaysApprove;
        public CountingWriteTool(bool alwaysApprove = false) => _alwaysApprove = alwaysApprove;

        public int CallCount { get; private set; }
        public string Kind => _alwaysApprove ? "git.merge_pr" : "git.open_pr";
        public string Description => _alwaysApprove ? "merge a PR" : "open a PR";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public JsonElement OutputSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        public bool IsReadOnly => false;
        public bool IsDestructive => true;
        public bool AlwaysRequiresApproval => _alwaysApprove;

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
