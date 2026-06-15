using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.SingleAgent;

/// <summary>
/// The <c>single-agent</c> projection (Rule 18.3 — one impl beside its variant folder): one agent works the
/// whole task in a single <c>agent.code</c> step. Emits the fixed-safe graph
/// <c>trigger.manual → agent.code → builtin.terminal</c>, whose <c>agent.code</c> node Config maps from the
/// context's <see cref="ResolvedAgentProfile"/> + <see cref="TaskLaunchSeed.Goal"/> onto the SAME keys
/// <c>AgentCodeNode</c> reads (harness ?? codex-cli, model, agentDefinitionId, modelCredentialId, runnerKind,
/// autonomyLevel, tools), and binds <c>repositoryId</c> as the node's INPUT from
/// <c>AgentProfile.RepositoryId ?? Seed.RepositoryId</c>. So a snapshot single-agent run executes IDENTICALLY
/// to an authored <c>agent.code</c> node — the executor sees the same task.
///
/// <para>Self-registers via <see cref="ISingletonDependency"/>; a new projection is a sibling builder folder,
/// never an edit here. The output ALWAYS passes <c>DefinitionValidator</c> (the build is parameter-driven over
/// a fixed three-node skeleton with no operator-typed graph, so it can't be malformed) — the same always-valid
/// contract <c>IWorkflowPlanProjector.Project</c> holds.</para>
/// </summary>
public sealed class SingleAgentDefinitionBuilder : IWorkflowDefinitionBuilder, ISingletonDependency
{
    /// <summary>The harness used when the profile names none — the agent.code catalog default (the const owns the wire string, Rule 8, so a rename can't silently drift).</summary>
    private const string DefaultHarness = CodexHarness.HarnessKind;

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
                Config = BuildAgentConfig(context), Inputs = BuildAgentInputs(context) },

        new() { Id = "done", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(),
                Inputs = TerminalInputs() },
    };

    private static IReadOnlyList<EdgeDefinition> BuildEdges() => new List<EdgeDefinition>
    {
        new() { From = "start", To = "agent" },
        new() { From = "agent", To = "done" },
    };

    /// <summary>
    /// The <c>agent.code</c> Config — maps the resolved profile + the seed goal onto the EXACT keys
    /// <c>AgentCodeNode</c> reads. Optional fields are emitted only when present (an absent key inherits the
    /// node's own default), so a bare profile produces the same minimal config an authored bare node would.
    /// </summary>
    private static JsonElement BuildAgentConfig(TaskBuildContext context)
    {
        var profile = context.AgentProfile;

        var config = new Dictionary<string, object?>
        {
            ["goal"] = context.Seed.Goal,
            ["harness"] = Harness(profile),
        };

        AddIfPresent(config, "model", NullIfBlank(profile?.Model));
        AddIfPresent(config, "agentDefinitionId", profile?.AgentDefinitionId?.ToString());
        AddIfPresent(config, "modelCredentialId", profile?.ModelCredentialId?.ToString());
        AddIfPresent(config, "runnerKind", NullIfBlank(profile?.RunnerKind));
        AddIfPresent(config, "autonomyLevel", NullIfBlank(profile?.AutonomyLevel));
        AddIfPresent(config, "tools", profile?.AllowedTools);

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The <c>agent.code</c> Inputs — the bound <c>repositoryId</c> from the profile, else the seed's repo. Absent when neither names one (an analysis-only run), matching an authored node with no repo bound.</summary>
    private static JsonElement BuildAgentInputs(TaskBuildContext context)
    {
        var repositoryId = context.AgentProfile?.RepositoryId ?? context.Seed.RepositoryId;

        var inputs = new Dictionary<string, object?>();

        AddIfPresent(inputs, "repositoryId", repositoryId?.ToString());

        return JsonSerializer.SerializeToElement(inputs);
    }

    /// <summary>The terminal surfaces the agent's result as the run's outputs — the SAME output keys agent.code emits, wired via {{ref}}.</summary>
    private static JsonElement TerminalInputs() => JsonSerializer.SerializeToElement(new
    {
        status = "{{nodes.agent.outputs.status}}",
        summary = "{{nodes.agent.outputs.summary}}",
        changedFiles = "{{nodes.agent.outputs.changedFiles}}",
        branch = "{{nodes.agent.outputs.branch}}",
    });

    /// <summary>The profile's harness, else the codex-cli default (matches AgentCodeNode's catalog default). Mirrors RealSupervisorActionExecutor.HarnessOf.</summary>
    private static string Harness(ResolvedAgentProfile? profile) =>
        !string.IsNullOrWhiteSpace(profile?.Harness) ? profile!.Harness! : DefaultHarness;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Add a config/input key only when the value is non-null — an absent key inherits the node's own default, keeping a bare profile's config minimal.</summary>
    private static void AddIfPresent(Dictionary<string, object?> bag, string key, object? value)
    {
        if (value != null) bag[key] = value;
    }

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}
