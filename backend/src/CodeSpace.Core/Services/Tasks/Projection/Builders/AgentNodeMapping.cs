using System.Text.Json;
using CodeSpace.Core.Services.Agents.Harnesses;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Enums;
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
/// profile produces the same minimal config a bare authored node would. The harness, when the profile names none, is
/// the shared <see cref="AgentHarnessDefaults.DefaultHarness"/> (Rule 8 — one source of truth across every projection +
/// the supervisor spawn, operator-overridable, codex-cli floor).</para>
/// </summary>
internal static class AgentNodeMapping
{
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
    public static JsonElement BuildAgentConfig(string goal, ResolvedAgentProfile? profile, string? mode = null, string? grounding = null, object? acceptance = null, IReadOnlyList<string>? criteria = null)
    {
        var config = new Dictionary<string, object?>
        {
            ["goal"] = ComposeGoalWithCriteria(ComposeGoal(goal, grounding), criteria),
            ["harness"] = Harness(profile),
        };

        // The task's OBJECTIVE acceptance (S5): a plan-map branch binds the ITEM's authored oracle
        // ("{{item.acceptance}}" — resolves per branch, null items omit at run time); the quick tier passes the
        // operator's checks floor as a literal spec. Null ⇒ omitted ⇒ no oracle ⇒ byte-identical.
        AddIfPresent(config, "acceptance", acceptance);

        AddIfPresent(config, "model", NullIfBlank(profile?.Model));
        AddIfPresent(config, "agentDefinitionId", profile?.AgentDefinitionId?.ToString());
        AddIfPresent(config, "modelCredentialId", profile?.ModelCredentialId?.ToString());
        AddIfPresent(config, "modelCredentialModelId", profile?.ModelCredentialModelId?.ToString());
        AddIfPresent(config, "runnerKind", NullIfBlank(profile?.RunnerKind));
        AddIfPresent(config, "autonomyLevel", NullIfBlank(profile?.AutonomyLevel));
        AddIfPresent(config, "tools", profile?.AllowedTools);
        // Per-run opt-in to the full MCP fabric; null ⇒ omitted ⇒ defer to the ambient flag ⇒ byte-identical. The same
        // key the supervisor agentProfile writes, so single-agent + spawned agents honour the operator's choice identically.
        AddIfPresent(config, "enableMcp", profile?.EnableMcp);
        // Per-run opt-in to publishing the diff as a branch; null ⇒ omitted ⇒ the node's mode-derived default / ambient
        // flag (ResolvePushBranch) ⇒ byte-identical. OR-gate: forces publish ON, never OFF.
        AddIfPresent(config, "pushBranch", profile?.PushBranch);
        // The output-review critic mode + reviewer model — the node reads these into AgentTask, where the executor runs
        // the critic at completion. None ⇒ omitted (the enum int) ⇒ no review ⇒ byte-identical.
        AddIfPresent(config, "outputReviewMode", profile?.OutputReviewMode is { } orm && orm != ReviewMode.None ? (int)orm : (int?)null);
        AddIfPresent(config, "reviewerModelId", profile?.ReviewerModelId?.ToString());
        // The bounded revise budget (S6) — how many oracle/critic failures may be fed back to the same agent in-run.
        // Null ⇒ omitted ⇒ the executor's default (1 under Improve, else 0) ⇒ byte-identical.
        AddIfPresent(config, "reviseRounds", profile?.ReviseRounds);
        // Multi-repo working-dir mode — the enum NAME ("WorkspaceRoot"/"PrimaryRepo"); null (Auto) ⇒ omitted ⇒ the
        // node's Auto default ⇒ byte-identical. AgentCodeNode reads it back via WorkspaceCwdModeWire.FromWire.
        AddIfPresent(config, "cwdMode", profile?.CwdMode?.ToString());
        // The operator's per-agent wall-clock (seconds): a positive value caps the run, an explicit 0 ⇒ NO wall-clock
        // (AgentCodeNode maps 0 → infinite); absent ⇒ the node's bounded 1h default. The same key an authored node reads.
        AddIfPresent(config, "timeoutSeconds", profile?.TimeoutSeconds);
        AddIfPresent(config, "mode", NullIfBlank(mode));

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>Append the operator's acceptance criteria to the agent's goal (S5b, the quick tier's steer — deep renders them into the supervisor prompt, standard into the planner prompt). Null / empty ⇒ verbatim (byte-identical).</summary>
    private static string ComposeGoalWithCriteria(string goal, IReadOnlyList<string>? criteria)
    {
        var kept = criteria?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        if (kept is not { Count: > 0 }) return goal;

        var builder = new System.Text.StringBuilder(goal);
        builder.AppendLine().AppendLine().AppendLine("Acceptance criteria (the operator's definition of done):");
        foreach (var c in kept) builder.AppendLine($"- {c.Trim()}");

        return builder.ToString().TrimEnd();
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
        var baseRefs = context.BaseRefs;

        var inputs = new Dictionary<string, object?>();

        AddIfPresent(inputs, "repositoryId", repositoryId?.ToString());
        // Session branch continuity: each related repo also clones at its own prior produced branch — the baseRefs map
        // threads a per-entry ref onto the relatedRepositories shape (omitted per entry when none, byte-identical).
        AddIfPresent(inputs, "relatedRepositories", AgentWorkspaceAuthoring.SerializeRelatedRepositories(context.AgentProfile?.RelatedRepositories, baseRefs));

        // …and clone the PRIMARY repo at its prior produced branch. Only meaningful with a primary repo; absent ⇒
        // omitted ⇒ the repo's default branch (byte-identical to a fresh launch).
        if (repositoryId is { } primaryId)
        {
            var primaryBaseRef = BaseRefFor(baseRefs, primaryId);
            AddIfPresent(inputs, "baseRef", primaryBaseRef);

            // The primary baseRef came from the SESSION base-refs map (a transient prior produced branch), so mark it
            // SOFT — the clone falls back to the default branch if it was pruned (a merged PR deletes it). Set only
            // alongside an actual baseRef; an author-pinned baseRef never carries this ⇒ stays a HARD ref (fail loud).
            if (primaryBaseRef is not null) inputs["baseRefFromSession"] = true;
        }

        return JsonSerializer.SerializeToElement(inputs);
    }

    /// <summary>The clone ref for a repo from the session base-refs map (null = absent ⇒ the repo's default branch).</summary>
    private static string? BaseRefFor(IReadOnlyDictionary<Guid, string>? baseRefs, Guid repositoryId) =>
        baseRefs is not null && baseRefs.TryGetValue(repositoryId, out var br) ? NullIfBlank(br) : null;

    /// <summary>The profile's harness, else the shared platform default (<see cref="AgentHarnessDefaults.DefaultHarness"/> — operator-overridable, codex-cli floor; the same source the supervisor spawn + planner projector use).</summary>
    private static string Harness(ResolvedAgentProfile? profile) =>
        !string.IsNullOrWhiteSpace(profile?.Harness) ? profile!.Harness! : AgentHarnessDefaults.DefaultHarness;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Add a config/input key only when the value is non-null — an absent key inherits the node's own default, keeping a bare profile's config minimal.</summary>
    private static void AddIfPresent(Dictionary<string, object?> bag, string key, object? value)
    {
        if (value != null) bag[key] = value;
    }
}
