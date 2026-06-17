using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// The L4-core supervisor brain's EXECUTION MECHANISM (PR-E E2): a re-entrant, durable, bounded TURN LOOP.
/// Each <c>RunAsync</c> is ONE turn — it rehydrates the run's decision tape from the durable
/// <c>SupervisorDecisionRecord</c> ledger (terminal decisions REPLAYED, never re-run), asks the
/// <c>ISupervisorDecider</c> for the next decision (E2 stub vocabulary: <c>plan</c> / <c>stop</c>),
/// claims + executes it exactly-once (E1 ledger hops), then either FINISHES (a terminal <c>stop</c> → a
/// terminal node result, the run completes via the normal walk) or PARKS on a <c>SupervisorDecision</c>
/// wait that SELF-ADVANCES into the next turn. The wait carries a PER-TURN IterationKey
/// (<c>&lt;nodeId&gt;#turn{N}</c>, mirroring flow.map's <c>&lt;mapId&gt;#&lt;i&gt;</c>) so each turn's wait
/// row is distinct.
///
/// <para>Mirrors <see cref="AgentCodeNode"/>'s re-entry shape (resume-from-payload vs return Suspend), but
/// where agent.code waits on an EXTERNAL agent run, the supervisor turn waits on NOTHING external — its
/// SupervisorDecision wait self-resumes. The node is a thin shell (Rule 16): it reads the run/team from the
/// system scope, then delegates the whole turn to the scoped <see cref="ISupervisorTurnService"/>, resolved
/// through an <see cref="IServiceScopeFactory"/> because the node itself is a DI singleton.</para>
///
/// <para>Flag-gated (<see cref="SupervisorLane.IsEnabled"/>): when the lane is OFF the node fails closed
/// rather than touching the ledger, so a deployment that never authors an agent.supervisor node is
/// byte-identical to today and an accidental one degrades to a clean node failure.</para>
///
/// Config: goal? (the run-level objective the supervisor pursues — the real LlmSupervisorDecider folds it into its prompt; the test stub follows a fixed script regardless)
/// Outputs: status · decision (the terminal decision kind) · reason (terminal reason) · turns (decided count) ·
/// integratedBranch (resolver loop #379, S5 — the run's final reviewable branch: the latest clean merge's, or a
/// VERIFIED resolver's own tested branch; "" when none, so a downstream git.open_pr can bind it directly)
/// </summary>
public sealed class AgentSupervisorNode : INodeRuntime
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentSupervisorNode(IServiceScopeFactory scopeFactory) { _scopeFactory = scopeFactory; }

    public string TypeKey => "agent.supervisor";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Supervisor",
        Category = "Agent",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        IconKey = "agent",
        Description = "The bounded durable supervisor: a re-entrant turn loop that decides + advances itself, recording every decision exactly-once. Self-advances between turns; the run shows Suspended while a turn is parked.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "goal": { "type": "string", "description": "The objective the supervisor pursues across its turns — the LLM decider folds it into the prompt that chooses each turn's decision (plan/spawn/retry/merge/ask_human/stop)." },
                "conversationId": { "type": "string", "format": "uuid", "x-selector": "conversation", "description": "Conversation the supervisor posts its ask_human + approval questions into (and parks for the answer). Leave empty to disable mid-loop human questions + approval gating." },
                "maxRounds": { "type": "integer", "minimum": 1, "maximum": 30, "description": "Hard cap on how many decisions (turns) the supervisor may take before it force-stops. Defaults to 30; you can only tighten it below the ceiling." },
                "maxParallelism": { "type": "integer", "minimum": 1, "maximum": 20, "description": "Max agents one spawn decision may fan out at once. Defaults to 20 (the schema ceiling)." },
                "maxTotalSpawns": { "type": "integer", "minimum": 1, "maximum": 1000, "description": "Max agents the whole run may spawn in total before it force-stops. Defaults to 50." },
                "maxCostUsd": { "type": "number", "minimum": 0, "description": "Optional USD budget for the run's realized agent token spend. Leave empty for no cost cap (the total-spawn cap still bounds the run). Spend above the budget force-stops the next spawn." },
                "maxNoProgressDecisions": { "type": "integer", "minimum": 1, "maximum": 30, "description": "Best-effort: force-stop after this many consecutive decisions produce no new agent result. Defaults to 8." },
                "approvalPolicy": { "type": "string", "enum": ["none", "spawns"], "description": "Whether a human must approve every spawn/retry before any agent is created. 'none' (default) = autonomous; 'spawns' = the supervisor parks on an approval card before spawning." },
                "allowedAgents": { "type": "array", "items": { "type": "string" }, "description": "Reserved — harness/agent kinds the supervisor may spawn. Stored; enforcement is a follow-up." },
                "allowedTools": { "type": "array", "items": { "type": "string" }, "description": "Tool allow-list every spawned agent is restricted to (e.g. Read, Grep, Bash). Empty = the harness default. Added to (not replacing) a persona's tools." },
                "acceptanceChecks": { "type": "array", "items": { "type": "string" }, "description": "Reserved — checks to verify before declaring success. Stored; the acceptance gate is a follow-up." },
                "agentProfile": {
                  "type": "object",
                  "additionalProperties": false,
                  "description": "Defaults every agent this supervisor spawns inherits — the supervisor's analogue of a coding-agent node's config. Leave empty for a bare codex-cli / analysis-only agent.",
                  "properties": {
                    "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The PRIMARY repository each spawned agent clones into its workspace. Leave empty for an analysis-only run with no repo." },
                    "relatedRepositories": {
                      "type": "array",
                      "description": "Multi-repo: each spawned agent ALSO clones these repositories alongside the primary (for a coordinated change across e.g. a frontend + backend). The primary is repositoryId; leave empty for a single-repo run.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "repositoryId": { "type": "string", "format": "uuid" },
                          "alias": { "type": "string", "description": "The short name + mount folder for this repo (e.g. 'api'). Defaults to repo-2, repo-3, …" },
                          "access": { "type": "string", "enum": ["read", "write"], "description": "read = context-only (default); write = the agent may edit + branch it." }
                        },
                        "required": ["repositoryId"]
                      }
                    },
                    "harness": { "type": "string", "x-selector": "harness", "description": "Which coding-agent CLI each spawned agent runs (e.g. Codex, Claude Code). Defaults to codex-cli." },
                    "model": { "type": "string", "description": "Model id within the harness's catalog. Leave empty to use the persona's model, or the harness default." },
                    "agentDefinitionId": { "type": "string", "format": "uuid", "x-selector": "agent", "description": "Agent persona each spawned agent embodies — its system prompt + model + tools become the defaults. Leave empty to configure inline." },
                    "modelCredentialId": { "type": "string", "format": "uuid", "x-selector": "modelCredential", "description": "Model credential each spawned agent authenticates with. Leave empty for the persona's or the team/operator default." },
                    "runnerKind": { "type": "string", "description": "Sandbox runner each spawned agent executes on (e.g. \"local\"). Defaults to the deployment default." },
                    "enableMcp": { "type": "boolean", "description": "Open the MCP tool-fabric endpoint for each spawned agent. Leave unset to defer to the deployment default." },
                    "autonomyLevel": { "type": "string", "enum": ["Confined", "Standard", "Trusted", "Unleashed"], "description": "How much each spawned agent may do — write scope + network. Confined: read-only, no network · Standard (default): workspace write, no network · Trusted: + network · Unleashed: highest." }
                  }
                }
              }
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "status":           { "type": "string" },
                "decision":         { "type": "string" },
                "reason":           { "type": "string" },
                "turns":            { "type": "integer" },
                "integratedBranch": { "type": "string" }
              }
            }
            """),
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Fail closed when the lane is off — never touch the ledger. A flag-OFF deployment that never
        // authors this node is byte-identical; an accidental one degrades to a clean node failure.
        if (!SupervisorLane.IsEnabled()) return NodeResult.Fail("The supervisor lane is disabled (set CODESPACE_SUPERVISOR_LANE_ENABLED to enable it).");

        if (!TryReadRunIdentity(context, out var supervisorRunId, out var teamId))
            return NodeResult.Fail("agent.supervisor could not read the run id / team id from the system scope.");

        var goalConfig = ReadGoalConfig(context.Config);
        var goal = ReadString(context.Config, "goal");
        var conversationId = ReadOptionalGuid(context.Config, "conversationId");

        // Durable re-entry guard (the spawn/retry async barrier): a PRIOR turn may have staged K AgentRun waits
        // that are still in flight. The spawn decision is already a SETTLED ledger row, so a naive rehydrate
        // would advance past it and run the NEXT turn — abandoning the running agents. If THIS node still has
        // pending AgentRun waits, the async turn is not done: re-park on them (the wait-for-all barrier resumes
        // once all complete), never advance. This makes a restart-mid-spawn re-suspend, not skip ahead.
        var (pendingAgents, pendingHumanToken) = await PendingParkStateAsync(supervisorRunId, teamId, context.NodeId, cancellationToken).ConfigureAwait(false);

        if (pendingAgents > 0) return ReparkOnPendingAgents(context, supervisorRunId, teamId, pendingAgents);

        // Durable human re-entry guard (E4): an ask_human decision is a SETTLED ledger row, so a restart while
        // parked on the human's answer would otherwise let the next turn run (re-claim handled, but the human
        // hasn't answered). The Action wait is still pending → re-park on the SAME wait, never advance + never
        // re-post the question.
        if (pendingHumanToken != null) return ReparkOnPendingHuman(context, pendingHumanToken);

        var result = await RunTurnAsync(supervisorRunId, teamId, context.NodeId, goal, conversationId, goalConfig, cancellationToken).ConfigureAwait(false);

        // The node re-runs on EITHER pass identically — the durable ledger (not ResumePayload) is the source
        // of truth, so a resumed pass (a self-advance marker, an agent-completion barrier resume, OR a human
        // answer) and a first pass both just run the next turn. The turn result picks the suspend path (E4):
        //  - FINISH  → a terminal stop; the node succeeds + the run completes via the normal walk.
        //  - PARK-ON-AGENTS → a spawn/retry staged K real AgentRun waits; the node suspends on THEM (no
        //    self-advance) and the wait-for-all barrier resumes the supervisor once all K agents complete.
        //  - PARK-ON-HUMAN → an ask_human posted a question card on a single Action wait; the node suspends
        //    on it and the human's answer (the existing ResumeByActionToken path) resumes the next turn.
        //  - SELF-ADVANCE → a synchronous plan/merge; the node parks on a SupervisorDecision wait that
        //    self-resumes into the next turn (the E2 path).
        if (result.IsFinished) return Finish(context.Logger, result);

        if (result.ParkedOnAgentWaits) return ParkOnAgentWaits(context.Logger, result);

        return result.ParkedOnHuman
            ? ParkOnHuman(context.Logger, result)
            : ParkSelfAdvance(context.Logger, context.NodeId, result);
    }

    /// <summary>Resolve the scoped turn service in its own DI scope (the node is a singleton) and run one turn.</summary>
    private async Task<SupervisorTurnResult> RunTurnAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, Guid? conversationId, SupervisorGoalConfig? goalConfig, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var turns = scope.ServiceProvider.GetRequiredService<ISupervisorTurnService>();

        return await turns.RunTurnAsync(supervisorRunId, teamId, nodeId, goal, conversationId, goalConfig, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read the run's pending-park state for this node (the durable re-entry guards): the count of still-pending AgentRun waits a prior spawn/retry staged, and the token of a still-pending ask_human Action wait. Read FIRST on re-entry so a restart re-parks on the existing wait rather than advancing past the (already-terminal) decision.</summary>
    private async Task<(int PendingAgents, string? PendingHumanToken)> PendingParkStateAsync(Guid supervisorRunId, Guid teamId, string nodeId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var turns = scope.ServiceProvider.GetRequiredService<ISupervisorTurnService>();

        var pendingAgents = await turns.CountPendingAgentWaitsAsync(supervisorRunId, nodeId, cancellationToken).ConfigureAwait(false);
        var pendingHumanToken = await turns.PendingHumanWaitTokenAsync(supervisorRunId, nodeId, cancellationToken).ConfigureAwait(false);

        return (pendingAgents, pendingHumanToken);
    }

    /// <summary>
    /// A prior spawn/retry turn's K AgentRun waits are STILL pending (a restart re-entered before they finished):
    /// re-park on them WITHOUT staging anything new + WITHOUT advancing. The marker tells the engine to record
    /// node.suspended + flip the run Suspended; the existing AgentRun waits + the wait-for-all barrier drive the
    /// resume once all complete. The IterationKey carries a stable <c>#repark</c> suffix so the marker row never
    /// collides with the turn's prior suspend record.
    /// </summary>
    private static NodeResult ReparkOnPendingAgents(NodeRunContext context, Guid supervisorRunId, Guid teamId, int pendingAgents)
    {
        context.Logger.LogInformation("agent.supervisor re-entered with {Count} agent run(s) still in flight; re-parking on them (durable barrier recovery)", pendingAgents);

        var marker = new SupervisorTurnContext { SupervisorRunId = supervisorRunId, TeamId = teamId, NodeId = context.NodeId };

        return NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.SupervisorAgentWaits,
            IterationKey = $"{context.NodeId}#repark",
            Payload = JsonSerializer.SerializeToElement(marker, AgentJson.Options),
        });
    }

    /// <summary>Terminal turn → the node succeeds; the run completes via the normal walk.</summary>
    private static NodeResult Finish(Microsoft.Extensions.Logging.ILogger logger, SupervisorTurnResult result)
    {
        logger.LogInformation("agent.supervisor finished: decision={Decision} reason={Reason}", result.DecisionKind, result.TerminalReason);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["status"] = JsonSerializer.SerializeToElement("Completed"),
            ["decision"] = JsonSerializer.SerializeToElement(result.DecisionKind),
            ["reason"] = JsonSerializer.SerializeToElement(result.TerminalReason ?? ""),
            ["turns"] = JsonSerializer.SerializeToElement((result.NextTurn?.TurnNumber ?? 0)),
            // The run's final reviewable integrated branch (S5) — "" when none, so a downstream git.open_pr binds it directly.
            ["integratedBranch"] = JsonSerializer.SerializeToElement(result.IntegratedBranch ?? ""),
        };

        return NodeResult.Ok(outputs);
    }

    /// <summary>
    /// SYNCHRONOUS non-terminal turn (plan / merge) → park on a SupervisorDecision wait that self-advances to
    /// the next turn. The wait's IterationKey is PER-TURN (<c>&lt;nodeId&gt;#turn{N}</c>) so each turn's row is
    /// distinct (must-fix #1). The payload is the next-turn context — a marker for observability; the node
    /// re-reads the ledger on re-entry, so the payload is not load-bearing for correctness.
    /// </summary>
    private static NodeResult ParkSelfAdvance(Microsoft.Extensions.Logging.ILogger logger, string nodeId, SupervisorTurnResult result)
    {
        var next = result.NextTurn!;

        logger.LogInformation("agent.supervisor self-advancing after decision={Decision}; advancing to turn {Turn}", result.DecisionKind, next.TurnNumber);

        return NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.SupervisorDecision,
            IterationKey = SupervisorOutcome.SelfAdvanceWaitKey(nodeId, next.TurnNumber),
            Payload = JsonSerializer.SerializeToElement(next, AgentJson.Options),
        });
    }

    /// <summary>
    /// ASYNC non-terminal turn (spawn / retry) → the executor ALREADY staged K real AgentRun waits keyed
    /// <c>&lt;nodeId&gt;#turn{N}#{k}</c>. The node suspends on a <c>SupervisorAgentWaits</c> marker the engine
    /// recognises as "the agent waits exist — record node.suspended + flip the run, but do NOT stage another
    /// wait and do NOT self-advance." The wait-for-all barrier (the agents' completion notifier →
    /// ResumeOnWaitCompletionAsync) resumes the supervisor once all K agents terminate, re-entering the node
    /// for the next turn. The per-turn IterationKey keeps this marker row distinct from a later turn's.
    /// </summary>
    private static NodeResult ParkOnAgentWaits(Microsoft.Extensions.Logging.ILogger logger, SupervisorTurnResult result)
    {
        var next = result.NextTurn!;

        logger.LogInformation("agent.supervisor parking on {Count} staged agent run(s) after decision={Decision}; the wait-for-all barrier resumes the next turn", result.ParkedAgentWaitCount, result.DecisionKind);

        return NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.SupervisorAgentWaits,
            IterationKey = $"{next.NodeId}#turn{next.TurnNumber}#park",
            Payload = JsonSerializer.SerializeToElement(next, AgentJson.Options),
        });
    }

    /// <summary>
    /// ASK-HUMAN park (E4) → the executor ALREADY posted the question card + staged a SINGLE <c>Action</c> wait
    /// keyed <c>&lt;nodeId&gt;#turn{N}#ask</c> (token-correlated to the card). The node suspends on the SAME
    /// <c>SupervisorAgentWaits</c> marker the agent barrier uses — "the executor already staged its external
    /// wait(s); record node.suspended + flip the run, but stage NO extra wait + DON'T self-advance." The human's
    /// answer rides the existing <c>ResumeByActionTokenAsync</c> path, which resolves EXACTLY that token's Action
    /// wait + re-dispatches; a SINGLE answer resumes the turn (NOT the wait-for-all barrier — there's one wait).
    /// Reusing the marker (vs returning a fresh Action suspend) is what avoids the engine staging a SECOND Action
    /// wait over the executor's — the resume is token-driven, so the marker's own key is irrelevant.
    /// </summary>
    private static NodeResult ParkOnHuman(Microsoft.Extensions.Logging.ILogger logger, SupervisorTurnResult result)
    {
        var next = result.NextTurn!;

        logger.LogInformation("agent.supervisor parking on a human answer after decision={Decision}; the answer resumes the next turn", result.DecisionKind);

        return SuspendOnExecutorStagedWaits(next.NodeId, next.TurnNumber, next, suffix: "ask");
    }

    /// <summary>
    /// A prior ask_human turn's <c>Action</c> wait is STILL pending (a restart re-entered before the human
    /// answered): re-park on it WITHOUT advancing + WITHOUT re-posting (the durable human-barrier recovery).
    /// Mirrors <see cref="ReparkOnPendingAgents"/> — the executor never re-runs (the decision is terminal); the
    /// existing Action wait + the human's eventual answer drive the resume. The token isn't needed here (the
    /// existing wait already carries it); the marker only records node.suspended.
    /// </summary>
    private static NodeResult ReparkOnPendingHuman(NodeRunContext context, string token)
    {
        context.Logger.LogInformation("agent.supervisor re-entered with a human question still unanswered; re-parking on it (durable recovery — no duplicate question)");

        var marker = new SupervisorTurnContext { NodeId = context.NodeId };

        return SuspendOnExecutorStagedWaits(context.NodeId, marker.TurnNumber, marker, suffix: "ask-repark");
    }

    /// <summary>
    /// Suspend on the executor-staged external wait(s) via the <c>SupervisorAgentWaits</c> marker: the engine
    /// records node.suspended + flips the run WITHOUT staging another wait + WITHOUT scheduling a self-advance
    /// (the executor already staged the real AgentRun / Action wait, and the agents' completion / the human's
    /// answer drives the resume). The per-suffix IterationKey keeps each marker row distinct. The payload is
    /// observability-only — the node re-reads the durable ledger + waits on re-entry.
    /// </summary>
    private static NodeResult SuspendOnExecutorStagedWaits(string nodeId, int turnNumber, SupervisorTurnContext payload, string suffix) =>
        NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.SupervisorAgentWaits,
            IterationKey = $"{nodeId}#turn{turnNumber}#{suffix}",
            Payload = JsonSerializer.SerializeToElement(payload, AgentJson.Options),
        });

    /// <summary>Read the supervisor run id (the WorkflowRun id) + team id from the engine-populated system scope.</summary>
    private static bool TryReadRunIdentity(NodeRunContext context, out Guid supervisorRunId, out Guid teamId)
    {
        supervisorRunId = Guid.Empty;
        teamId = Guid.Empty;

        return TryReadGuid(context.Scope.Sys, SystemScopeKeys.WorkflowRunId, out supervisorRunId)
               && TryReadGuid(context.Scope.Sys, SystemScopeKeys.TeamId, out teamId);
    }

    private static bool TryReadGuid(IReadOnlyDictionary<string, JsonElement> bag, string key, out Guid value)
    {
        value = Guid.Empty;

        if (!bag.TryGetValue(key, out var v)) return false;

        return v.ValueKind == JsonValueKind.String ? Guid.TryParse(v.GetString(), out value) : v.TryGetGuid(out value);
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Read an optional uuid config value (the ask_human conversation), null when absent / empty / not a valid id (mirrors agent.code's ReadOptionalGuid).</summary>
    private static Guid? ReadOptionalGuid(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id) ? id : null;

    /// <summary>
    /// Parse the operator's GOAL + limits + approval policy out of the node's raw config (PR-E E5) — the
    /// turn service resolves it into a <c>SupervisorGoalPlan</c> + reads every bound from it. Deserialised
    /// case-insensitively (mirrors the engine's <c>MapConfig</c> read), tolerating a malformed object → a
    /// null config → all SupervisorLane defaults (pre-E5 behaviour). The lenient clamp/default lives in the
    /// plan, not here — the node only lifts the bytes.
    /// </summary>
    private static SupervisorGoalConfig? ReadGoalConfig(IReadOnlyDictionary<string, JsonElement> bag)
    {
        try { return JsonSerializer.Deserialize<SupervisorGoalConfig>(JsonSerializer.Serialize(bag), GoalConfigOptions); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions GoalConfigOptions = new() { PropertyNameCaseInsensitive = true };
}
