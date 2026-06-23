using System.Text.Json;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders;

/// <summary>
/// The ONE place an <c>agent.code</c> node's Config / Inputs are mapped from a <see cref="ResolvedAgentProfile"/>
/// (Rule 16 — shared builder logic has a single home, never copy-pasted across builders). Both the
/// <c>single-agent</c> projection (one whole-task agent) and the <c>plan-map-synth</c> projection (a per-subtask
/// fan-out body) emit IDENTICAL agent.code config shape — only the GOAL differs (a literal seed goal vs a
/// <c>{{item}}</c> binding the map resolves per branch). Keeping the mapping here guarantees a snapshot agent runs
/// the SAME way an authored agent.code node does, regardless of which projection emitted it.
///
/// <para>Optional fields are emitted only when present (an absent key inherits the node's own default), so a bare
/// profile produces the same minimal config a bare authored node would. <see cref="DefaultHarness"/> matches the
/// agent.code catalog default (the const owns the wire string, Rule 8, so a rename can't silently drift).</para>
/// </summary>
internal static class AgentNodeMapping
{
    /// <summary>The harness used when the profile names none — the agent.code catalog default.</summary>
    public const string DefaultHarness = CodexHarness.HarnessKind;

    /// <summary>
    /// The <c>agent.code</c> Config — maps the resolved profile onto the EXACT keys <c>AgentCodeNode</c> reads,
    /// with the supplied <paramref name="goal"/> (a literal for single-agent, a <c>{{item}}</c> binding for a map
    /// body) and an optional <paramref name="mode"/> (the model-authored intent — a <c>{{item.mode}}</c> binding the
    /// dynamic fan-out body passes per branch; omitted when null/blank so the two existing callers emit identical
    /// JSON). Optional knobs are added only when present so a bare profile stays minimal.
    /// <para><paramref name="grounding"/> is the session thread-context (a continuing turn's prior-work digest);
    /// when present it is PREPENDED to the goal (the agent's prompt) so a follow-up builds on earlier turns. Null
    /// (a fresh launch) leaves the goal byte-identical.</para>
    /// </summary>
    public static JsonElement BuildAgentConfig(string goal, ResolvedAgentProfile? profile, string? mode = null, string? grounding = null)
    {
        var config = new Dictionary<string, object?>
        {
            ["goal"] = ComposeGoal(goal, grounding),
            ["harness"] = Harness(profile),
        };

        AddIfPresent(config, "model", NullIfBlank(profile?.Model));
        AddIfPresent(config, "agentDefinitionId", profile?.AgentDefinitionId?.ToString());
        AddIfPresent(config, "modelCredentialId", profile?.ModelCredentialId?.ToString());
        AddIfPresent(config, "modelCredentialModelId", profile?.ModelCredentialModelId?.ToString());
        AddIfPresent(config, "runnerKind", NullIfBlank(profile?.RunnerKind));
        AddIfPresent(config, "autonomyLevel", NullIfBlank(profile?.AutonomyLevel));
        AddIfPresent(config, "tools", profile?.AllowedTools);
        AddIfPresent(config, "mode", NullIfBlank(mode));

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>
    /// Prepend the session thread-context <paramref name="grounding"/> to a node's <paramref name="goal"/> (the
    /// agent / supervisor prompt), with a clear separator + framing so a continuing turn builds on the prior work
    /// rather than restarting. Null / blank grounding returns the goal verbatim (a fresh launch is byte-identical).
    /// The shared composition point so single-agent, map, and supervisor projections inject context identically.
    /// </summary>
    public static string ComposeGoal(string goal, string? grounding) =>
        string.IsNullOrWhiteSpace(grounding)
            ? goal
            : $"{grounding}\n\n---\nNow address this follow-up for the SAME thread — continue from the prior work above, do not start over:\n\n{goal}";

    /// <summary>
    /// The <c>agent.code</c> Inputs — the bound <c>repositoryId</c> (primary) from the profile, else the seed's repo,
    /// plus the multi-repo <c>relatedRepositories</c> when the profile authored any (the SAME {repositoryId, alias,
    /// access} shape <c>AgentCodeNode</c> reads + the editor emits, so the projection lane and the authored node
    /// produce an identical workspace). Absent when neither names a repo (an analysis-only run); the related-repos
    /// key is omitted entirely when none, keeping a single-repo projection byte-identical.
    /// </summary>
    public static JsonElement BuildAgentInputs(TaskBuildContext context)
    {
        var repositoryId = context.AgentProfile?.RepositoryId ?? context.Seed.RepositoryId;

        var inputs = new Dictionary<string, object?>();

        AddIfPresent(inputs, "repositoryId", repositoryId?.ToString());
        AddIfPresent(inputs, "relatedRepositories", AgentWorkspaceAuthoring.SerializeRelatedRepositories(context.AgentProfile?.RelatedRepositories));

        // Session branch continuity: clone the primary repo at the prior turn's produced branch. Only meaningful with
        // a primary repo; absent ⇒ omitted ⇒ the repo's default branch (byte-identical to a fresh launch).
        if (repositoryId is not null) AddIfPresent(inputs, "baseRef", NullIfBlank(context.PrimaryBaseRef));

        return JsonSerializer.SerializeToElement(inputs);
    }

    /// <summary>The profile's harness, else the codex-cli default (matches AgentCodeNode's catalog default; mirrors RealSupervisorActionExecutor.HarnessOf).</summary>
    private static string Harness(ResolvedAgentProfile? profile) =>
        !string.IsNullOrWhiteSpace(profile?.Harness) ? profile!.Harness! : DefaultHarness;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Add a config/input key only when the value is non-null — an absent key inherits the node's own default, keeping a bare profile's config minimal.</summary>
    private static void AddIfPresent(Dictionary<string, object?> bag, string key, object? value)
    {
        if (value != null) bag[key] = value;
    }
}
