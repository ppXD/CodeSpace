using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using System.Text.Json;

namespace CodeSpace.Core.Services.Completion;

public interface IModeProfileRegistry
{
    /// <summary>The mode's declared profile, or null when the mode has no conformance story — the terminal authority fails CLOSED on null.</summary>
    ModeProfile? Resolve(string mode);

    /// <summary>Every registered mode key — the claim board iterates the closed vocabulary (the generic mode is deliberately absent).</summary>
    IReadOnlyCollection<string> RegisteredModes { get; }
}

/// <summary>The mode keys the classifier derives and the registry declares. Wire-stable (they will land on receipts and qualification records); renaming one is a data migration.</summary>
public static class RunModeKeys
{
    public const string Supervisor = "supervisor";
    public const string PlanMap = "plan-map";
    public const string SingleAgent = "single-agent";
    public const string Generic = "generic";
}

/// <summary>
/// P4 (Lock Clause 4): the CLOSED mode-profile vocabulary with committed stage declarations — no deployment
/// toggles; a profile change is a reviewed edit to this table. Today's honest standings: the supervisor lane
/// holds Enforceable standing (the first admitted Enforced cohort — Q3), plan-map and single-agent hold
/// Open/Shadow evidence (their stage chains are exercised by the live gate and the shadow sweep); a GENERIC
/// graph — arbitrary nodes with no agent lane — is deliberately UNREGISTERED: its runs have no conformance
/// story, so an Enforced generic run parks Unsupported instead of terminalizing a Success nothing qualified.
/// Every profile is validated TOTAL over the ten stages at construction, so adding an eleventh stage breaks the
/// build here instead of leaving cells silently unmapped.
/// </summary>
public sealed class ModeProfileRegistry : IModeProfileRegistry, ISingletonDependency
{
    private static readonly IReadOnlyDictionary<string, ModeProfile> Registered = new[]
    {
        // The supervisor lane exercises the FULL chain: contracts staked at spawn, plans on the tape, integration
        // via merge/resolve, per-unit + stop verification, capture + publish + handoff receipts, assessment + the
        // arbitrated terminal — and holds ENFORCEABLE standing (Q3): the first admitted Enforced cohort. The
        // accumulated evidence Enforceable names: the P2b park canaries, the P4 stage-gate parks, the durable
        // park→Continue re-arbitration, and the whole-loop Enforced E2E proving both the unbacked park and the
        // fully-evidenced Success. Demotion is the same one-line reviewed edit — the authority's readiness gate
        // then re-parks the cohort's in-flight Enforced rows immediately.
        Profile(RunModeKeys.Supervisor, ProtocolReadiness.Enforceable, required: new[]
        {
            CompletionStage.Contract, CompletionStage.Plan, CompletionStage.Execute, CompletionStage.Integrate,
            CompletionStage.Verify, CompletionStage.Capture, CompletionStage.Deliver, CompletionStage.Handoff,
            CompletionStage.Assess, CompletionStage.Terminal,
        }),
        // Plan-map fans out per item and synthesizes — no in-run integration of branches yet (the P4 PlanMap
        // integrated-candidate arc adds it); the stage is authorized off by SERVER POLICY until that lands,
        // never silently absent.
        Profile(RunModeKeys.PlanMap, ProtocolReadiness.Open, required: new[]
        {
            CompletionStage.Contract, CompletionStage.Plan, CompletionStage.Execute,
            CompletionStage.Verify, CompletionStage.Capture, CompletionStage.Deliver, CompletionStage.Handoff,
            CompletionStage.Assess, CompletionStage.Terminal,
        }),
        // Single-agent has no plan and nothing to integrate — one unit, its own branch.
        Profile(RunModeKeys.SingleAgent, ProtocolReadiness.Shadow, required: new[]
        {
            CompletionStage.Contract, CompletionStage.Execute,
            CompletionStage.Verify, CompletionStage.Capture, CompletionStage.Deliver, CompletionStage.Handoff,
            CompletionStage.Assess, CompletionStage.Terminal,
        }),
    }.ToDictionary(p => p.Mode, StringComparer.Ordinal);

    public ModeProfile? Resolve(string mode) => Registered.GetValueOrDefault(mode);

    public IReadOnlyCollection<string> RegisteredModes => Registered.Keys.ToArray();

    private static ModeProfile Profile(string mode, ProtocolReadiness readiness, CompletionStage[] required)
    {
        var stages = Enum.GetValues<CompletionStage>().ToDictionary(
            s => s,
            s => required.Contains(s) ? StageRequiredness.Required : StageRequiredness.ServerPolicyAuthorizedNotApplicable);

        return new ModeProfile { Mode = mode, Stages = stages, Readiness = readiness };
    }
}

/// <summary>
/// The v1 derivation of a run's OPERATING MODE — from its launch-stamped projection kind when the tasks lane
/// launched it, else from the frozen definition's own node shape (an authored workflow declares its lane by the
/// nodes it contains). Pure, so the mapping pins without a database. Order matters and is deliberate: a graph
/// carrying a supervisor node IS a supervisor run whatever else it contains; map outranks plain agents.
/// </summary>
public static class RunModeClassifier
{
    public static string Derive(string? projectionKind, WorkflowDefinition definition)
    {
        if (!string.IsNullOrEmpty(projectionKind))
            return projectionKind switch
            {
                Messages.Tasks.TaskProjectionKinds.Supervisor => RunModeKeys.Supervisor,
                Messages.Tasks.TaskProjectionKinds.SingleAgent => RunModeKeys.SingleAgent,
                Messages.Tasks.TaskProjectionKinds.PlanMapSynth or Messages.Tasks.TaskProjectionKinds.PlanMapDynamic or Messages.Tasks.TaskProjectionKinds.CoordinatedLoop => RunModeKeys.PlanMap,
                _ => RunModeKeys.Generic,
            };

        var keys = definition.Nodes.Select(n => n.TypeKey).ToHashSet(StringComparer.Ordinal);

        if (keys.Contains("agent.supervisor")) return RunModeKeys.Supervisor;
        if (keys.Contains("flow.map")) return RunModeKeys.PlanMap;
        if (keys.Contains("agent.run")) return RunModeKeys.SingleAgent;

        return RunModeKeys.Generic;
    }

    /// <summary>Derive from the run row's frozen definition json — the terminal authority's entry point. Unparseable json reads Generic (fail-closed downstream: Generic is unregistered).</summary>
    public static string DeriveFromJson(string? projectionKind, string? definitionJson)
    {
        if (!string.IsNullOrEmpty(projectionKind)) return Derive(projectionKind, EmptyDefinition);

        if (string.IsNullOrWhiteSpace(definitionJson)) return RunModeKeys.Generic;

        try
        {
            return JsonSerializer.Deserialize<WorkflowDefinition>(definitionJson, Workflows.WorkflowJson.Options) is { } definition
                ? Derive(projectionKind: null, definition)
                : RunModeKeys.Generic;
        }
        catch (JsonException)
        {
            return RunModeKeys.Generic;
        }
    }

    private static readonly WorkflowDefinition EmptyDefinition = new() { Nodes = [], Edges = [] };
}
