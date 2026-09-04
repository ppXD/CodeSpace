using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.SingleAgent;

/// <summary>
/// The <c>single-agent</c> projection (Rule 18.3 — one impl beside its variant folder): one agent works the
/// whole task in a single <c>agent.run</c> step. Emits the fixed-safe graph
/// <c>trigger.manual → agent.run → builtin.terminal</c>, whose <c>agent.run</c> node Config maps from the
/// context's <see cref="ResolvedAgentProfile"/> + <see cref="TaskLaunchSeed.Goal"/> onto the SAME keys
/// <c>AgentCodeNode</c> reads (via the shared <see cref="AgentNodeMapping"/>), and binds <c>repositoryId</c> as
/// the node's INPUT from <c>AgentProfile.RepositoryId ?? Seed.RepositoryId</c>. So a snapshot single-agent run
/// executes IDENTICALLY to an authored <c>agent.run</c> node — the executor sees the same task.
///
/// <para>Self-registers via <see cref="ISingletonDependency"/>; a new projection is a sibling builder folder,
/// never an edit here. The output ALWAYS passes <c>DefinitionValidator</c> (the build is parameter-driven over
/// a fixed three-node skeleton with no operator-typed graph, so it can't be malformed) — the same always-valid
/// contract <c>IWorkflowPlanProjector.Project</c> holds.</para>
/// </summary>
public sealed class SingleAgentDefinitionBuilder : IWorkflowDefinitionBuilder, ISingletonDependency
{
    public string ProjectionKind => TaskProjectionKinds.SingleAgent;

    /// <summary>The repo-relative file a shape-derived contract grades. One conventional path, named in the goal, so an answer / report / findings run has a file the oracle can actually read.</summary>
    public const string DeliverableFileName = "DELIVERABLE.md";

    /// <summary>The route's deliverable shape, normalized — an unknown / absent shape reads as <c>code</c>, today's behaviour.</summary>
    private static string Shape(TaskBuildContext context) => DeliverableShapes.Normalize(context.Route.DeliverableShape);

    /// <summary>The operator's EXECUTABLE floor, blanks dropped. Non-empty ⇒ the operator authored the oracle and it wins over any shape-derived one.</summary>
    private static IReadOnlyList<string>? OperatorArgv(TaskBuildContext context) =>
        context.AcceptanceChecks?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() is { Count: > 0 } command ? command : null;

    /// <summary>
    /// THIS agent's objective oracle. Precedence, and the whole point of the shape axis:
    /// <list type="number">
    /// <item>The operator's acceptance-checks floor, when authored (S5) — an argv graded as <c>TestsPass</c>, regardless
    /// of shape. An operator who wrote a command asked for THAT command; a classifier never overrides it.</item>
    /// <item>Otherwise, for a NON-code shape (answer / document / research), the <c>LlmJudge</c> contract over the
    /// declared deliverable file — because a question, a report and an investigation have no test to pass, and grading
    /// them as if they did is why they were graded by nothing at all.</item>
    /// <item>Otherwise (a code shape with no floor) NO oracle — byte-identical to before this axis existed.</item>
    /// </list>
    /// </summary>
    private static object? QuickAcceptance(TaskBuildContext context)
    {
        if (OperatorArgv(context) is { } command) return new { command, kind = "TestsPass" };

        if (Shape(context) == DeliverableShapes.Code) return null;

        return new { command = new[] { DeliverableFileName }, kind = "LlmJudge", rubric = new { criteria = RubricCriteria(context) } };
    }

    /// <summary>
    /// What the judge grades the deliverable against: the operator's own acceptance criteria, one binary criterion
    /// each (the default threshold means EVERY one must be met). With none authored, the goal itself is the single
    /// criterion — a weaker bar than an operator-written rubric, but an honest one, and the first bar these runs
    /// have ever had.
    /// </summary>
    private static IReadOnlyList<object> RubricCriteria(TaskBuildContext context)
    {
        var criteria = context.AcceptanceCriteria?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        if (criteria is not { Count: > 0 })
            return new object[] { new { id = "goal", requirement = $"The deliverable fully and directly addresses the task: {context.Seed.Goal.Trim()}" } };

        return criteria.Select((c, i) => (object)new { id = $"c{i + 1}", requirement = c.Trim() }).ToList();
    }

    /// <summary>The deliverable path to name in the agent's goal — only when the oracle actually reads a file (a shape-derived contract). Null for an operator argv / a code shape ⇒ the goal is composed byte-identically.</summary>
    private static string? DeliverablePath(TaskBuildContext context) =>
        OperatorArgv(context) is null && Shape(context) != DeliverableShapes.Code ? DeliverableFileName : null;

    public WorkflowDefinition Build(TaskBuildContext context) => new()
    {
        SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
        CompletionMode = context.CompletionMode,
        Nodes = BuildNodes(context),
        Edges = BuildEdges(),
    };

    private static IReadOnlyList<NodeDefinition> BuildNodes(TaskBuildContext context) => new List<NodeDefinition>
    {
        new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

        new() { Id = "agent", TypeKey = "agent.run", Label = "Run the task", Retry = AgentNodeMapping.DefaultRetry,
                // P5-4: the acceptance's provenance — stamped Operator ONLY for the operator's own launch argv, so the
                // staked requirement rows never credit the operator with a shape-derived contract the server composed.
                // B2: the SHAPE decides the agent's mode (a question is not a coding run) and, absent an operator floor,
                // which oracle grades it — a code-shaped launch with no floor stays byte-identical (no mode, no oracle).
                Config = AgentNodeMapping.BuildAgentConfig(context.Seed.Goal, context.AgentProfile, mode: DeliverableShapes.AgentModeFor(context.Route.DeliverableShape),
                                                           grounding: context.GroundingContext, acceptance: QuickAcceptance(context), criteria: context.AcceptanceCriteria,
                                                           acceptanceAuthority: OperatorArgv(context) is null ? null : nameof(Messages.Contracts.ContractAuthority.Operator),
                                                           deliverablePath: DeliverablePath(context)), Inputs = AgentNodeMapping.BuildAgentInputs(context) },

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
    /// The terminal surfaces the agent's result as the run's outputs — the SAME output keys agent.run emits, wired
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
