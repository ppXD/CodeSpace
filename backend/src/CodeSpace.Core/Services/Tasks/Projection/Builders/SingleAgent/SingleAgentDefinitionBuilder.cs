using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.SingleAgent;

/// <summary>
/// The <c>single-agent</c> projection (Rule 18.3 — one impl beside its variant folder): one agent works the
/// whole task in a single <c>agent.code</c> step. Emits the fixed-safe graph
/// <c>trigger.manual → agent.code → builtin.terminal</c>, whose <c>agent.code</c> node Config maps from the
/// context's <see cref="ResolvedAgentProfile"/> + <see cref="TaskLaunchSeed.Goal"/> onto the SAME keys
/// <c>AgentCodeNode</c> reads (via the shared <see cref="AgentNodeMapping"/>), and binds <c>repositoryId</c> as
/// the node's INPUT from <c>AgentProfile.RepositoryId ?? Seed.RepositoryId</c>. So a snapshot single-agent run
/// executes IDENTICALLY to an authored <c>agent.code</c> node — the executor sees the same task.
///
/// <para>Self-registers via <see cref="ISingletonDependency"/>; a new projection is a sibling builder folder,
/// never an edit here. The output ALWAYS passes <c>DefinitionValidator</c> (the build is parameter-driven over
/// a fixed three-node skeleton with no operator-typed graph, so it can't be malformed) — the same always-valid
/// contract <c>IWorkflowPlanProjector.Project</c> holds.</para>
/// </summary>
public sealed class SingleAgentDefinitionBuilder : IWorkflowDefinitionBuilder, ISingletonDependency
{
    public string ProjectionKind => TaskProjectionKinds.SingleAgent;

    public WorkflowDefinition Build(TaskBuildContext context) => new()
    {
        SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
        Nodes = BuildNodes(context),
        Edges = BuildEdges(),
    };

    private static IReadOnlyList<NodeDefinition> BuildNodes(TaskBuildContext context) => new List<NodeDefinition>
    {
        new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

        new() { Id = "agent", TypeKey = "agent.code", Label = "Run the task",
                Config = AgentNodeMapping.BuildAgentConfig(context.Seed.Goal, context.AgentProfile, grounding: context.GroundingContext), Inputs = AgentNodeMapping.BuildAgentInputs(context) },

        new() { Id = "done", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(),
                Inputs = TerminalInputs(IsMultiRepo(context)) },
    };

    private static IReadOnlyList<EdgeDefinition> BuildEdges() => new List<EdgeDefinition>
    {
        new() { From = "start", To = "agent" },
        new() { From = "agent", To = "done" },
    };

    /// <summary>A run is multi-repo when the profile authored related repos — known here at projection time, so the terminal can surface the per-repo change set ONLY when it can be non-empty (a single-repo run never authors related repos).</summary>
    private static bool IsMultiRepo(TaskBuildContext context) => context.AgentProfile?.RelatedRepositories is { Count: > 0 };

    /// <summary>
    /// The terminal surfaces the agent's result as the run's outputs — the SAME output keys agent.code emits, wired
    /// via {{ref}}. A MULTI-repo run ALSO surfaces <c>repositoryResults</c> (each repo's produced branch) so a session
    /// follow-up can continue each repo from its own prior branch (a session-branch resolver reads it from OutputsJson).
    /// <para>It is bound ONLY for a multi-repo run: a single-repo run never emits <c>repositoryResults</c> from the
    /// agent node, so binding it unconditionally would resolve to a <c>repositoryResults: null</c> key in EVERY
    /// single-repo run's OutputsJson — not byte-identical. Gating on the authored multi-repo intent keeps a single-repo
    /// run's output untouched.</para>
    /// </summary>
    private static JsonElement TerminalInputs(bool multiRepo)
    {
        var inputs = new Dictionary<string, object?>
        {
            ["status"] = "{{nodes.agent.outputs.status}}",
            ["summary"] = "{{nodes.agent.outputs.summary}}",
            ["changedFiles"] = "{{nodes.agent.outputs.changedFiles}}",
            ["branch"] = "{{nodes.agent.outputs.branch}}",
        };

        if (multiRepo) inputs["repositoryResults"] = "{{nodes.agent.outputs.repositoryResults}}";

        return JsonSerializer.SerializeToElement(inputs);
    }

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}
