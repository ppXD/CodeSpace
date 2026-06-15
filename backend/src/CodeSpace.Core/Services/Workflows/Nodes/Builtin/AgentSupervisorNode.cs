using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
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
/// Config: goal? (the run-level objective the supervisor pursues; opaque to E2's stub decider)
/// Outputs: status · decision (the terminal decision kind) · reason (terminal reason) · turns (decided count)
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
                "goal": { "type": "string", "description": "The objective the supervisor pursues across its turns. Optional in E2 (the stub decider follows a fixed plan→stop script regardless)." }
              }
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "status":   { "type": "string" },
                "decision": { "type": "string" },
                "reason":   { "type": "string" },
                "turns":    { "type": "integer" }
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

        var goal = ReadString(context.Config, "goal");

        var result = await RunTurnAsync(supervisorRunId, teamId, goal, cancellationToken).ConfigureAwait(false);

        // The node re-runs on EITHER pass identically — the durable ledger (not ResumePayload) is the source
        // of truth, so a resumed pass (ResumePayload present, a bare self-advance marker) and a first pass
        // both just run the next turn. The turn result decides: finish or park for the next turn.
        return result.IsFinished
            ? Finish(context.Logger, result)
            : Park(context.Logger, context.NodeId, result);
    }

    /// <summary>Resolve the scoped turn service in its own DI scope (the node is a singleton) and run one turn.</summary>
    private async Task<SupervisorTurnResult> RunTurnAsync(Guid supervisorRunId, Guid teamId, string goal, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var turns = scope.ServiceProvider.GetRequiredService<ISupervisorTurnService>();

        return await turns.RunTurnAsync(supervisorRunId, teamId, goal, cancellationToken).ConfigureAwait(false);
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
        };

        return NodeResult.Ok(outputs);
    }

    /// <summary>
    /// Non-terminal turn → park on a SupervisorDecision wait that self-advances to the next turn. The wait's
    /// IterationKey is PER-TURN (<c>&lt;nodeId&gt;#turn{N}</c>) so each turn's row is distinct (must-fix #1).
    /// The payload is the next-turn context — a marker for observability; the node re-reads the ledger on
    /// re-entry, so the payload is not load-bearing for correctness.
    /// </summary>
    private static NodeResult Park(Microsoft.Extensions.Logging.ILogger logger, string nodeId, SupervisorTurnResult result)
    {
        var next = result.NextTurn!;

        logger.LogInformation("agent.supervisor parking after decision={Decision}; advancing to turn {Turn}", result.DecisionKind, next.TurnNumber);

        return NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.SupervisorDecision,
            IterationKey = $"{nodeId}#turn{next.TurnNumber}",
            Payload = JsonSerializer.SerializeToElement(next, AgentJson.Options),
        });
    }

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
}
