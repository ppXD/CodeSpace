using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
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
                "maxParallelism": { "type": "integer", "minimum": 1, "maximum": 20, "description": "Max agents one spawn decision may fan out at once. Defaults to 20 (the schema ceiling)." },
                "maxTotalSpawns": { "type": "integer", "minimum": 1, "maximum": 1000, "description": "Max agents the whole run may spawn in total before it force-stops. Defaults to 50." },
                "maxCostUsd": { "type": "number", "minimum": 0, "description": "Optional USD budget for the run's realized agent token spend. Leave empty for no cost cap (the total-spawn cap still bounds the run). Spend above the budget force-stops the next spawn." },
                "maxNoProgressDecisions": { "type": "integer", "minimum": 1, "maximum": 30, "description": "Best-effort: force-stop after this many consecutive decisions produce no new agent result. Defaults to 8." },
                "approvalPolicy": { "type": "string", "enum": ["none", "spawns"], "description": "Whether a human must approve every spawn/retry before any agent is created. 'none' (default) = autonomous; 'spawns' = the supervisor parks on an approval card before spawning." },
                "allowedAgents": { "type": "array", "items": { "type": "string" }, "description": "Reserved — harness/agent kinds the supervisor may spawn. Stored; enforcement is a follow-up." },
                "allowedTools": { "type": "array", "items": { "type": "string" }, "description": "Tool allow-list every spawned agent is restricted to (e.g. Read, Grep, Bash). Empty = the harness default. Added to (not replacing) a persona's tools." },
                "supervisorModelId": { "type": "string", "format": "uuid", "x-selector": "credentialedModel", "description": "REQUIRED. The credentialed model the supervisor's own decision-making brain runs on — pick one from a credential's model list (distinct from the agents it spawns). The run fails closed if absent or unresolvable." },
                "decisionReviewMode": { "type": "integer", "enum": [0, 1, 2], "description": "How an independent critic reviews each decision before its side effect: 0 = None (default, no critic), 1 = Gate, 2 = Improve (one bounded re-decide against the critique). Leave unset for no review." },
                "planReviewMode": { "type": "integer", "enum": [0, 1, 2], "default": 0, "description": "PLAN-scoped critic: reviews ONLY the supervisor's plan decisions (the tier-generic plan critic) — 0 = off, 1 = Gate, 2 = Improve. A plan uses this when set, else falls under decisionReviewMode." },
                "reviewerModelId": { "type": "string", "format": "uuid", "x-selector": "credentialedModel", "description": "The credentialed model the decision critic runs on (ideally distinct from the brain). Leave empty to auto-pick the team's strongest structured-eligible model. Only used when decisionReviewMode is not None." },
                "allowedModelIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "x-selector": "credentialedModel", "description": "Allowed model pool for the agents the supervisor dispatches — a multi-select of credentialed models. Every dispatched agent's model must be one of these (and runs on that model's credential). Empty = the pool is ALL the team's credentialed models." },
                "allowedAgentDefinitionIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "x-selector": "agent", "description": "Allowed agent (persona) pool for the agents the supervisor dispatches — a multi-select of Agent personas. Every dispatched agent's persona must be one of these. Empty = the pool is ALL the team's personas." },
                "acceptanceChecks": { "type": "array", "items": { "type": "string" }, "description": "The operator acceptance FLOOR — an argv (e.g. [\"sh\",\"check.sh\"]) run against the run's reviewable head at the terminal stop. Mandatory when set: a non-zero exit fails the stop and withholds the reviewable branch, so success can't be declared on an unverified head." },
                "acceptanceCriteria": { "type": "array", "items": { "type": "string" }, "description": "Free-text acceptance CRITERIA the operator wants met (e.g. \"tests pass\", \"PR opened\") — rendered into the decider prompt as the definition of done the supervisor targets. NOT executed (unlike acceptanceChecks, which is an argv floor)." },
                "requirePlanConfirmation": { "type": "boolean", "description": "When true, every AUTHORED plan version parks the run on a confirmation card before any agent is created — an approving answer releases execution; any other answer is revision feedback the supervisor folds into a revised plan. Off (the default) = fully autonomous planning." },
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
                    "timeoutSeconds": { "type": "integer", "description": "Wall-clock cap for each spawned agent, in seconds. Positive caps the run; 0 = no wall-clock (bounded only by the stall watchdog + cost cap); leave empty for the bounded 1h default." },
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
                "status":           { "type": "string", "description": "Terminal status: 'Completed', or 'AcceptanceFailed' when the stop's model-authored acceptance check did not pass (the reviewable branch is then withheld)." },
                "decision":         { "type": "string" },
                "reason":           { "type": "string" },
                "turns":            { "type": "integer" },
                "integratedBranch": { "type": "string" },
                "repositoryId": { "type": "string", "description": "Single-repo only: this run's configured primary repository (echoes the config's repositoryId) — the owner of integratedBranch, which is a bare branch name with no repository of its own." },
                "repositoryBranches": { "type": "array", "items": { "type": "object" }, "description": "Multi-repo only: per-repo {repositoryId, alias, sourceBranch, targetBranch} reconciled heads + PR bases. Binds VERBATIM into git.open_change_set's repositories input (it reads sourceBranch/targetBranch) — wire agent.supervisor.repositoryBranches → git.open_change_set.repositories to open one PR per repo. Omitted for a single-repo run (which uses integratedBranch)." }
              }
            }
            """),
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRunIdentity(context, out var supervisorRunId, out var teamId))
            return NodeResult.Fail("agent.supervisor could not read the run id / team id from the system scope.");

        var goalConfig = ReadGoalConfig(context.Config);
        var goal = ReadString(context.Config, "goal");
        var conversationId = ReadOptionalGuid(context.Config, "conversationId");
        var repositoryId = ReadOptionalGuid(context.Config, "repositoryId");

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

        SupervisorTurnResult result;

        try
        {
            result = await RunTurnAsync(supervisorRunId, teamId, context.NodeId, goal, conversationId, goalConfig, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmApiException fault) when (SupervisorInfraPark.IsParkable(fault.Category))
        {
            // P1.1 — park, don't die: the model plane is transiently down (the in-call retry already rode out the
            // short blips), so the run WAITS OUT the outage on a durable deadline ladder and re-enters the SAME
            // turn on wake, instead of terminalizing hours of work. Only past the whole 24h window does it stop —
            // honestly, as a degraded Stopped.
            return await ParkForInfraOrStopAsync(context, supervisorRunId, teamId, goal, goalConfig, repositoryId, fault, cancellationToken).ConfigureAwait(false);
        }

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
        if (result.IsFinished) return Finish(context.Logger, result, repositoryId);

        if (result.ParkedOnAgentWaits) return ParkOnAgentWaits(context.Logger, result);

        return result.ParkedOnHuman
            ? ParkOnHuman(context.Logger, result)
            : ParkSelfAdvance(context.Logger, context.NodeId, result);
    }

    /// <summary>
    /// The model-plane outage path (P1.1): compute the next park on the exponential ladder from the resume payload's
    /// durable marker; park the run on a <c>SupervisorInfraPark</c> wait whose DEADLINE is the wake (nothing else
    /// resolves it) and whose TimeoutPayload carries the ladder position — or, once the whole window is exhausted,
    /// force an HONEST degraded stop through the ledger (the node then reports <c>Stopped</c> + the reason).
    /// </summary>
    private async Task<NodeResult> ParkForInfraOrStopAsync(NodeRunContext context, Guid supervisorRunId, Guid teamId, string goal, SupervisorGoalConfig? goalConfig, Guid? repositoryId, LlmApiException fault, CancellationToken cancellationToken)
    {
        var state = SupervisorInfraPark.Next(context.ResumePayload, DateTimeOffset.UtcNow);

        if (state.WindowExhausted)
        {
            context.Logger.LogWarning("agent.supervisor run {RunId}: the model plane stayed unavailable past the whole {Window} park window — stopping honestly", supervisorRunId, SupervisorInfraPark.MaxParkWindow);

            var stopped = await ForceStopAsync(supervisorRunId, teamId, context.NodeId, goal, goalConfig, SupervisorStopReasons.ModelPlaneUnavailable, cancellationToken).ConfigureAwait(false);

            return Finish(context.Logger, stopped, repositoryId);
        }

        var delay = SupervisorInfraPark.DelayFor(state.Parks);
        var marker = SupervisorInfraPark.Marker(state, fault.Message);

        context.Logger.LogWarning("agent.supervisor run {RunId}: brain call hit a {Category} infra fault — parking {Delay} (park {Parks} since {First:o}) instead of failing the run", supervisorRunId, fault.Category, delay, state.Parks, state.FirstParkedAtUtc);

        return NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.SupervisorInfraPark,
            IterationKey = $"{context.NodeId}#infra{state.Parks}",
            Payload = marker,
            DeadlineAt = DateTimeOffset.UtcNow + delay,
            TimeoutPayload = marker,
        });
    }

    /// <summary>Resolve the scoped turn service in its own DI scope (the node is a singleton) and force the honest degraded stop (the P1.1 window-exhausted ending).</summary>
    private async Task<SupervisorTurnResult> ForceStopAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, SupervisorGoalConfig? goalConfig, string reason, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var turns = scope.ServiceProvider.GetRequiredService<ISupervisorTurnService>();

        return await turns.ForceStopAsync(supervisorRunId, teamId, nodeId, goal, goalConfig, reason, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Terminal turn → the node succeeds; the run completes via the normal walk. Internal so a unit test can pin the output bag (notably the multi-repo `repositoryBranches` emit-only-when-non-empty byte-identity guard).</summary>
    internal static NodeResult Finish(Microsoft.Extensions.Logging.ILogger logger, SupervisorTurnResult result, Guid? repositoryId = null)
    {
        logger.LogInformation("agent.supervisor finished: decision={Decision} reason={Reason}", result.DecisionKind, result.TerminalReason);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["status"] = JsonSerializer.SerializeToElement(TerminalStatus(result)),
            ["decision"] = JsonSerializer.SerializeToElement(result.DecisionKind),
            // A GaveUp stop carries no payload reason — fall back to the classification's (the non-success outcome
            // label, e.g. "no-decision"), so a Stopped status always names why.
            ["reason"] = JsonSerializer.SerializeToElement(result.TerminalReason ?? result.StopClassification?.Reason ?? ""),
            ["turns"] = JsonSerializer.SerializeToElement((result.NextTurn?.TurnNumber ?? 0)),
            // The run's final reviewable integrated branch (S5) — "" when none, so a downstream git.open_pr binds it directly.
            ["integratedBranch"] = JsonSerializer.SerializeToElement(result.IntegratedBranch ?? ""),
            // The single-repo run's PRIMARY repository (config, not a computed fact) — "" when the run has none (an
            // analysis-only run), so a downstream reader (the Room's Open-PR action) can resolve integratedBranch's
            // owning repository without re-reading the node's config (which isn't durably stored per run).
            ["repositoryId"] = JsonSerializer.SerializeToElement(repositoryId?.ToString() ?? ""),
        };

        // Multi-repo (S7-D1): the per-repo integrated branches a downstream per-repo PR-open targets. Emitted ONLY when
        // non-empty — a single-repo run surfaces the single integratedBranch above, so its output bag is byte-identical.
        if (result.RepositoryBranches.Count > 0)
            outputs["repositoryBranches"] = JsonSerializer.SerializeToElement(result.RepositoryBranches, AgentJson.Options);

        return NodeResult.Ok(outputs);
    }

    /// <summary>
    /// The terminal <c>status</c> output, in verdict-strength order: a FAILED model-authored acceptance grade is an
    /// OBJECTIVE miss → <c>AcceptanceFailed</c> (L4 P1; the reviewable branch is withheld upstream); a degraded stop
    /// (server-FORCED by a bound/budget/governance trip, or a model GIVE-UP — classified by the shared
    /// <c>SupervisorOutcome.ClassifyStop</c>) → <c>Stopped</c>, because a run that ended mid-way must never claim
    /// <c>Completed</c>; only a genuine model-authored success stop reports <c>Completed</c>.
    /// </summary>
    private static string TerminalStatus(SupervisorTurnResult result) =>
        result.AcceptancePassed == false ? "AcceptanceFailed"
        : result.StopClassification?.Degraded == true ? "Stopped"
        : "Completed";

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

    /// <summary>Case-insensitive options for deserializing the node's <see cref="SupervisorGoalConfig"/> bag (the camelCase config keys the ConfigSchema declares).</summary>
    private static readonly JsonSerializerOptions GoalConfigOptions = new() { PropertyNameCaseInsensitive = true };
}
