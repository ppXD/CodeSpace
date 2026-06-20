using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (high fidelity, Rule 12): the agent-grain Decision substrate (D2) driven through the REAL
/// <see cref="McpRequestHandler"/> + REAL <see cref="ToolCallLedgerService"/> + REAL <see cref="ChatBotService"/> +
/// REAL <see cref="MessageInteractionService"/> respond path (routing a <c>DecisionRequestTarget</c> to the REAL
/// <see cref="DecisionRequestResolver"/>) + the shared singleton <see cref="ToolApprovalWaiterRegistry"/> over real
/// Postgres. A <c>decision.request</c> parks an AwaitingApproval row + posts a typed-options card + BLOCKS the
/// synchronous call; a human's option click resolves it to a <see cref="CodeSpace.Messages.Decisions.DecisionAnswer"/>
/// the blocked call returns mid-run. Covers: the happy answer path; the gate-exemption (a Confined-tier agent can still
/// ASK); idempotent re-raise (AC1 — one card, replays the answer); resolve-once (AC2); a free-text answer; and the
/// no-surface fail-closed refusal.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class McpDecisionFlowTests
{
    private readonly PostgresFixture _fixture;

    public McpDecisionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_answered_decision_unblocks_the_call_with_the_typed_answer_and_marks_the_row_succeeded()
    {
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var handler = DecisionHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId);

        // decision.request BLOCKS until a human answers → run it on a background task.
        var call = Task.Run(() => CallToolAsync(handler, new { question = "Which path?", decisionType = "choose_one", options = new[] { new { id = "a", label = "A" }, new { id = "b", label = "B" } }, recommendedOption = "a" }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        await RespondAsync(teamId, messageId, "b", ownerId, comment: "B is safer");

        var result = await call;

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("an answered decision returns the typed answer, not an error");
        var answer = result.GetProperty("structuredContent");
        answer.GetProperty("selectedOptions")[0].GetString().ShouldBe("b", "the chosen option id flows back to the agent");
        answer.GetProperty("freeText").GetString().ShouldBe("B is safer");
        answer.GetProperty("answeredBy").GetString().ShouldBe("human");

        var row = await ReadRowAsync(ledgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the AwaitingApproval decision row flips to Succeeded via the answer CAS");
        row.ToolKind.ShouldBe("decision.request");
    }

    [Fact]
    public async Task A_decision_is_gate_exempt_so_even_a_Confined_agent_can_ask()
    {
        // THE gate-exemption proof: a decision is an ASK, not a side effect. At Confined — the tier that flat-DENIES
        // every gated tool — decision.request must still PARK + block (intercepted before the autonomy gate), not be
        // refused. If the special-case were missing it would fall to the gate / CallAsync and never post a card.
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var handler = DecisionHandler(scope, AgentAutonomyLevel.Confined, teamId, runId, channelId);

        var call = Task.Run(() => CallToolAsync(handler, new { question = "ok?", decisionType = "confirm" }));

        var (_, messageId) = await WaitForPostedCardAsync(teamId, runId);   // a card posted ⇒ Confined did NOT deny the ask

        await RespondAsync(teamId, messageId, DecisionRequestResolver.FreeTextResponseKey, ownerId, comment: "go");

        var result = await call;

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("a Confined agent's decision is answered, never denied");
        result.GetProperty("structuredContent").GetProperty("freeText").GetString().ShouldBe("go");
    }

    [Fact]
    public async Task A_re_raise_with_the_same_args_is_idempotent_one_card_and_replays_the_answer()
    {
        // AC1: the same decision.request (same dedupe key) returns the SAME decision, never a new question. A bound
        // timeout parks the row; a human answers out-of-band; the model re-issues the exact call → it replays the
        // answer, and exactly ONE card was ever posted across the timeout + re-call.
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        var previous = Environment.GetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar);
        Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, "1");   // tiny bound → time out without a human

        try
        {
            using var scope = _fixture.BeginScope();
            var handler = DecisionHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId);

            object args = new { question = "ship?", decisionType = "confirm" };

            var first = await CallToolAsync(handler, args);

            first.GetProperty("isError").GetBoolean().ShouldBeTrue("the bound elapsed with no answer → the pending ticket");
            Text(first).ShouldContain("pending", customMessage: "the pending ticket tells the model to re-issue");

            var ledgerId = (await ReadRunRowsAsync(teamId, runId)).ShouldHaveSingleItem().Id;
            (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the row stays parked for a later answer — never stranded");
            var messageId = (await ReadRowAsync(ledgerId)).ApprovalMessageId!.Value;

            await RespondAsync(teamId, messageId, DecisionRequestResolver.FreeTextResponseKey, ownerId, comment: "yes");

            var reCall = await CallToolAsync(handler, args);

            reCall.GetProperty("isError").GetBoolean().ShouldBeFalse("the re-call replays the now-answered decision");
            reCall.GetProperty("structuredContent").GetProperty("freeText").GetString().ShouldBe("yes");

            (await ReadRunCardCountAsync(teamId, channelId)).ShouldBe(1, "exactly one decision card per (run, dedupe key) across the timeout + re-call");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, previous);
        }
    }

    [Fact]
    public async Task Two_concurrent_answers_resolve_the_decision_exactly_once()
    {
        // AC2: a cross-pod / dup-click race resolves the decision exactly once — the answer CAS leaves one winner.
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var member2 = await SeedConversationMemberAsync(teamId, ownerId, channelId);
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var handler = DecisionHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId);

        var call = Task.Run(() => CallToolAsync(handler, new { question = "pick", decisionType = "choose_one", options = new[] { new { id = "a", label = "A" }, new { id = "b", label = "B" } } }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        var outcomes = await Task.WhenAll(
            TryRespondAsync(teamId, messageId, "a", ownerId),
            TryRespondAsync(teamId, messageId, "b", member2));

        var result = await call;

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        outcomes.Count(ok => ok).ShouldBe(1, "exactly one answer wins; the loser is rejected as already-resolved");
        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the row is Succeeded exactly once");
    }

    [Fact]
    public async Task A_run_with_no_decision_surface_fail_closes_with_no_card_and_no_row()
    {
        var (teamId, _, _) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        // Governance ON + collaborators wired, but NO decision conversation → fail-closed (the agent must not proceed as if it asked).
        var handler = new McpRequestHandler(new SingleToolRegistry(new DecisionRequestTool()), AgentAutonomyLevel.Standard, teamId, null, runId,
            scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true, approvalConversationId: null,
            scope.Resolve<IChatBotService>(), scope.Resolve<IToolApprovalWaiterRegistry>(), scope.Resolve<IInteractionComponentRegistry>());

        var result = await CallToolAsync(handler, new { question = "ok?" });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(result).ShouldContain("decision surface", customMessage: "no surface → fail-closed, the agent does not silently proceed");
        (await ReadRunRowsAsync(teamId, runId)).ShouldBeEmpty("no surface → no ledger row");
    }

    [Fact]
    public async Task An_option_label_that_echoes_a_run_secret_is_redacted_on_the_card()
    {
        // The redaction invariant must hold for the agent-authored option LABELS too — not just the card body. A model
        // that echoes its own key into an option label must not leak it onto the human card. The secret lives ONLY in
        // the label (the question is clean), so a clean InteractionJson proves the LABEL was redacted.
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();
        const string secret = "sk-live-DEADBEEF0123456789abcdef";

        using var scope = _fixture.BeginScope();
        var handler = DecisionHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId, new SecretRedactor(new[] { secret }));

        var call = Task.Run(() => CallToolAsync(handler, new { question = "pick a path", decisionType = "choose_one", options = new[] { new { id = "a", label = $"use {secret}" }, new { id = "b", label = "B" } } }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        var interactionJson = await ReadInteractionJsonAsync(messageId);
        interactionJson.ShouldNotContain(secret, customMessage: "an echoed run secret in an option label must be redacted before reaching the human card — the same invariant the card body upholds");

        // The STASHED envelope (read back by the cross-grain queue, another human surface) must be redacted too.
        (await ReadRowAsync(ledgerId)).DecisionEnvelopeJson.ShouldNotContain(secret, customMessage: "the queue's stashed envelope must be redacted just like the card");

        await RespondAsync(teamId, messageId, "a", ownerId);
        await call;   // unblock the parked call so the test doesn't leak a blocked task
    }

    [Fact]
    public async Task A_parked_decision_stashes_its_envelope_so_the_queue_can_project_it()
    {
        // D3 prerequisite: the real handler park must persist the DecisionRequest envelope on the ledger row (the
        // node-grain stashes it in the wait payload; this is the symmetric agent-grain stash the queue reads).
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var handler = DecisionHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId);

        var call = Task.Run(() => CallToolAsync(handler, new { question = "Stash check: which path?", decisionType = "choose_one", options = new[] { new { id = "a", label = "A" } }, riskLevel = "high", policy = "supervisor_first" }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        var envelopeJson = (await ReadRowAsync(ledgerId)).DecisionEnvelopeJson;
        envelopeJson.ShouldNotBeNull("the park must stash the envelope so the queue can project the decision without reading the card");

        var env = JsonSerializer.Deserialize<CodeSpace.Messages.Decisions.DecisionRequest>(envelopeJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        env.Question.ShouldBe("Stash check: which path?");
        env.RiskLevel.ShouldBe("high");
        env.Policy.ShouldBe(CodeSpace.Messages.Decisions.DecisionPolicies.HumanRequired, "the agent-grain park applies the D4 fail-closed floor — high risk clamps the declared supervisor_first → human_required");
        env.ResumeBackend.ShouldBe(CodeSpace.Messages.Decisions.DecisionResumeBackends.ToolLedger);

        await RespondAsync(teamId, messageId, "a", ownerId);
        await call;
    }

    [Fact]
    public async Task A_string_isSideEffecting_option_is_parsed_as_the_safety_flag_and_floors_to_human()
    {
        // isSideEffecting is a fail-closed security signal feeding the policy floor — a model emitting the STRING "true"
        // (not a boolean) must NOT silently drop it (which would let an irreversible decision auto-resolve). An
        // otherwise-auto-able supervisor_first decision with a string-"true" side-effecting option must stash human_required.
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var handler = DecisionHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId);

        var call = Task.Run(() => CallToolAsync(handler, new
        {
            question = "deploy?", decisionType = "choose_one",
            options = new object[] { new { id = "a", label = "A" }, new { id = "b", label = "B", isSideEffecting = "true" } },
            recommendedOption = "a", blockingReason = "ready", riskLevel = "low", policy = "supervisor_first",
        }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        var env = JsonSerializer.Deserialize<CodeSpace.Messages.Decisions.DecisionRequest>((await ReadRowAsync(ledgerId)).DecisionEnvelopeJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        env.Options[1].IsSideEffecting.ShouldBeTrue("a string \"true\" isSideEffecting is parsed as the safety flag, not dropped");
        env.Policy.ShouldBe(CodeSpace.Messages.Decisions.DecisionPolicies.HumanRequired, "a side-effecting option floors the decision to human even when declared supervisor_first");

        await RespondAsync(teamId, messageId, "a", ownerId);
        await call;
    }

    [Fact]
    public async Task A_queue_answer_resolves_an_agent_decision_and_unblocks_the_mid_run_call()
    {
        // D3b: answering an agent-grain decision through the QUEUE service (not the chat card) must resolve it and wake
        // the blocked mid-run call with the typed answer — the same durable CAS the card uses, so it's resolve-once.
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var handler = DecisionHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId);

        var call = Task.Run(() => CallToolAsync(handler, new { question = "pick", decisionType = "choose_one", options = new[] { new { id = "a", label = "A" }, new { id = "b", label = "B" } } }));

        var (ledgerId, _) = await WaitForPostedCardAsync(teamId, runId);

        (await AnswerViaQueueAsync(ledgerId, new[] { "b" }, null, teamId, ownerId)).Outcome.ShouldBe(DecisionAnswerOutcome.Answered);

        var result = await call;
        result.GetProperty("isError").GetBoolean().ShouldBeFalse("the queue answer unblocks the mid-run call");
        result.GetProperty("structuredContent").GetProperty("selectedOptions")[0].GetString().ShouldBe("b");

        (await ReadRowAsync(ledgerId)).Status.ShouldBe(ToolCallLedgerStatus.Succeeded);

        (await AnswerViaQueueAsync(ledgerId, new[] { "a" }, null, teamId, ownerId)).Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "resolve-once: a second answer is an idempotent no-op");
    }

    [Fact]
    public async Task An_unknown_option_in_a_queue_answer_is_rejected_as_invalid()
    {
        var (teamId, ownerId, channelId) = await SeedTeamChannelAsync();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var handler = DecisionHandler(scope, AgentAutonomyLevel.Standard, teamId, runId, channelId);

        var call = Task.Run(() => CallToolAsync(handler, new { question = "pick", decisionType = "choose_one", options = new[] { new { id = "a", label = "A" } } }));

        var (ledgerId, messageId) = await WaitForPostedCardAsync(teamId, runId);

        (await AnswerViaQueueAsync(ledgerId, new[] { "nope" }, null, teamId, ownerId)).Outcome.ShouldBe(DecisionAnswerOutcome.Invalid, "an option that isn't one of the choices is rejected");

        // A FOREIGN team can't answer it — and can't tell it exists.
        var (otherTeam, _, _) = await SeedTeamChannelAsync();
        (await AnswerViaQueueAsync(ledgerId, new[] { "a" }, null, otherTeam, ownerId)).Outcome.ShouldBe(DecisionAnswerOutcome.NotFound, "a cross-team answer is a clean not-found");

        await RespondAsync(teamId, messageId, "a", ownerId);   // resolve for real so the call doesn't leak
        await call;
    }

    private async Task<AnswerDecisionResult> AnswerViaQueueAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, Guid teamId, Guid actorUserId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IDecisionAnswerService>().AnswerAsync(decisionId, selectedOptions, freeText, teamId, actorUserId, CancellationToken.None);
    }

    // ─── Build the handler with the decision surface ─────────────────────────────

    private McpRequestHandler DecisionHandler(ILifetimeScope scope, AgentAutonomyLevel autonomy, Guid teamId, Guid runId, Guid channelId, SecretRedactor? redactor = null) =>
        new(new SingleToolRegistry(new DecisionRequestTool()), autonomy, teamId, redactor, runId,
            scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true, approvalConversationId: channelId,
            scope.Resolve<IChatBotService>(), scope.Resolve<IToolApprovalWaiterRegistry>(), scope.Resolve<IInteractionComponentRegistry>());

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

        throw new TimeoutException($"The decision card for run {runId} was not posted within 10s — the blocked decision.request should record AwaitingApproval + stamp the message id before parking.");
    }

    private async Task<ToolCallLedger> ReadRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    private async Task<string> ReadInteractionJsonAsync(Guid messageId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<CodeSpaceDbContext>().Message.AsNoTracking().SingleAsync(m => m.Id == messageId)).InteractionJson ?? "";
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

    private static async Task<JsonElement> CallToolAsync(McpRequestHandler handler, object arguments)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "decision.request", arguments } });
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
            db.User.Add(new User { Id = ownerId, Email = $"dec-{ownerId:N}@test.local", Name = $"dec-{ownerId:N}" });

            teamId = Guid.NewGuid();
            db.Team.Add(new Team { Id = teamId, Slug = $"dec-{teamId:N}", Name = "Decision Team", Kind = TeamKind.Workspace, OwnerUserId = ownerId });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = ownerId, Role = TeamRole.Owner });

            await db.SaveChangesAsync();
        }

        using var s2 = _fixture.BeginScope();
        var slug = "dec-" + Guid.NewGuid().ToString("N")[..8];
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
            db.User.Add(new User { Id = memberId, Email = $"dm2-{memberId:N}@test.local", Name = $"dm2-{memberId:N}" });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = memberId, Role = TeamRole.Member });
            await db.SaveChangesAsync();
        }

        using var s2 = _fixture.BeginScope();
        await s2.Resolve<IConversationService>().AddMemberAsync(teamId, ownerId, channelId, memberId, CancellationToken.None);
        return memberId;
    }

    private sealed class SingleToolRegistry : IAgentToolRegistry
    {
        private readonly IAgentTool _tool;
        public SingleToolRegistry(IAgentTool tool) => _tool = tool;
        public IReadOnlyList<IAgentTool> All => new[] { _tool };
        public IAgentTool? Resolve(string kind) => kind == _tool.Kind ? _tool : null;
    }
}
