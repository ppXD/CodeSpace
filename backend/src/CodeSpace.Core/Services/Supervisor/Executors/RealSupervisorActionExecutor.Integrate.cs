using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// SOTA #3 — the OPT-IN integrate + synthesis augmentation of the supervisor <c>merge</c> (Rule 10 <c>.Integrate.cs</c>).
/// When the integrate gate is on, the deterministic fold (<c>.Merge.cs</c>) is augmented so multi-agent fan-out
/// INTEGRATES rather than narrates:
/// <list type="bullet">
///   <item><b>integration</b> (model-free) — the K agents' diffs are integrated ON DISK into one reviewable branch via
///         <see cref="IBranchIntegrator"/>, base-anchored + fail-safe (a conflict keeps the K branches + reports it; no
///         corrupt merge, no clobber). Best-effort: a git infrastructure failure is recorded, never crashes the turn.</item>
///   <item><b>synthesis</b> (model) — a 2nd <see cref="ILLMClient"/> reduce over the K REAL diffs producing a coherent
///         combined summary. Validated now with a deterministic fake at the <see cref="ILLMClient"/> seam (it proves the
///         diffs are threaded into the prompt); real-model quality is deferred to the cassette tier.</item>
/// </list>
///
/// <para>The gate is fail-closed (<see cref="AgentRunExecutor.ShouldIntegrate"/> — the ambient env flag OR the profile's
/// per-run opt-in). With it OFF the outcome is byte-identical to pre-SOTA-#3: no clone, no LLM call, just the fold.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    /// <summary>The model id the synthesis reduce uses when the profile names none — a REAL provider model (never the literal "default", which a real API rejects), matching every other LLM caller's default.</summary>
    private const string SynthesisDefaultModel = "claude-sonnet-4-5";

    /// <summary>Layer the integration + synthesis keys onto the fold outcome — ONLY when the gate is on AND there are agents to combine. A no-op (returns immediately) keeps the gate-OFF path byte-identical.</summary>
    private async Task AugmentWithIntegrationAndSynthesisAsync(Dictionary<string, object?> outcome, SupervisorTurnContext context, IReadOnlyList<MergedAgent> merged, CancellationToken cancellationToken)
    {
        var profile = context.AgentProfile;

        if (!AgentRunExecutor.ShouldIntegrate(perRunOptIn: profile?.IntegrateBranches == true)) return;
        if (merged.Count == 0) return;

        // Synthesis (facet b) reads the diffs — no repo needed; runs whenever the gate is on. Best-effort: a model
        // failure degrades to a note, NEVER crashing the merge turn (which would strand the decision row Running).
        outcome["synthesis"] = await TrySynthesizeAsync(context.Goal, merged, profile, cancellationToken).ConfigureAwait(false);

        // Integration (facet a) writes a branch — only with a resolvable repository. EXCEPT when the conflict was
        // already RESOLVED: a VERIFIED resolver's own tested branch IS the reconciled merge, so re-running the
        // integrator over the original conflicting branches would just re-conflict (resolver loop #379, S5). In that
        // case surface the resolver branch as a Clean integration without touching git.
        if (profile?.RepositoryId is { } repoId)
            outcome["integration"] = AcceptedResolutionBranch(context) is { } resolvedBranch
                ? ResolutionIntegration(resolvedBranch)
                : await IntegrateMergedAsync(repoId, context, merged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The branch a VERIFIED resolver produced — but ONLY when the most recent agent-staging decision (spawn / retry /
    /// resolve) was a verified <c>resolve</c> (resolver loop #379, S5). That means the conflict is reconciled into the
    /// resolver's OWN tested branch and a <c>merge</c> must surface IT, not re-integrate. Null when the latest staging
    /// was a normal spawn/retry (there's fresh agent work to combine → run the integrator) or the resolution wasn't
    /// verified (the safety floor already withheld acceptance). Reads only durable folded state — pure + replay-safe.
    /// </summary>
    private static string? AcceptedResolutionBranch(SupervisorTurnContext context)
    {
        var lastStaging = context.PriorDecisions.LastOrDefault(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind));

        // The most-recent agent-staging decision must ITSELF be the accepted resolution — a spawn/retry after it means
        // fresh work to combine, so the integrator runs. The "verified resolve → its branch" rule is the SHARED
        // SupervisorOutcome.ResolvedBranch (the same encoding the node-output reader uses, so the two never drift).
        return lastStaging is null ? null : SupervisorOutcome.ResolvedBranch(lastStaging);
    }

    /// <summary>
    /// The integration block for an ACCEPTED resolution (S5): the resolver's own tested branch surfaced as a Clean
    /// integration WITHOUT re-running the integrator. <c>status</c> + <c>integratedBranch</c> are the load-bearing
    /// fields (read by <see cref="SupervisorOutcome.ReadIntegration"/>); <c>via</c> + <c>reason</c> are DESCRIPTIVE
    /// audit metadata in the persisted ledger only — no reader or gate branches on them (the acceptance decision is
    /// keyed off the resolve verdict via <see cref="SupervisorOutcome.ResolvedBranch"/>, NEVER off <c>via</c>). No
    /// <c>outcomes</c> array (no per-contribution apply happened — the resolver did the reconciliation).
    /// </summary>
    private static object ResolutionIntegration(string resolvedBranch) => new
    {
        status = "Clean",
        integratedBranch = resolvedBranch,
        via = "resolution",   // descriptive audit only (see doc) — no code branches on this; the gate is the resolve verdict
        reason = "a verified resolver agent reconciled the conflicting branches into one tested branch",
    };

    // ── Facet (b): the model synthesis reduce over the K real diffs ──────────────────

    /// <summary>Best-effort wrapper: synthesis is a non-essential enrichment, so ANY failure (a model 4xx/5xx, a missing key, a transport / serialization fault) degrades to a note — it must never escape and strand the turn. Cancellation still propagates.</summary>
    private async Task<object> TrySynthesizeAsync(string goal, IReadOnlyList<MergedAgent> merged, SupervisorAgentProfile? profile, CancellationToken cancellationToken)
    {
        try
        {
            return await SynthesizeAsync(goal, merged, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Supervisor synthesis reduce failed; keeping the deterministic fold + on-disk integration");
            return new { note = "synthesis unavailable", error = ex.Message };
        }
    }

    private async Task<object> SynthesizeAsync(string goal, IReadOnlyList<MergedAgent> merged, SupervisorAgentProfile? profile, CancellationToken cancellationToken)
    {
        // The synthesis is a plain-TEXT reduce, so it prefers a dedicated text-completion provider (the established
        // synth seam) and falls back to any registered client — in production the structured-capable provider also
        // serves text. This intentionally differs from the decider/planner's structured-first resolution: those NEED
        // structured output; a text reduce does not. A deployment with no LLM provider degrades to a note.
        var client = _llm.All.FirstOrDefault(c => c is not IStructuredLLMClient) ?? _llm.All.FirstOrDefault();

        if (client is null) return new { note = "no LLM provider available for synthesis" };

        var request = new LLMCompletionRequest
        {
            Model = string.IsNullOrWhiteSpace(profile?.Model) ? SynthesisDefaultModel : profile!.Model!,
            SystemPrompt = "You are combining the work of several parallel coding agents into ONE coherent change. Each agent's unified diff follows. Produce a concise synthesis: what the combined change does, how the pieces fit, and any overlaps or risks a reviewer should check. Do not invent changes that are not in the diffs.",
            UserPrompt = BuildSynthesisPrompt(goal, merged),
        };

        var completion = await client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        return new { text = completion.Text, model = completion.Model };
    }

    /// <summary>The synthesis user prompt — the goal + each agent's status/summary AND its REAL unified diff (the hunk bodies), so the reduce reasons over what the agents actually changed, not just their self-reported summaries.</summary>
    private static string BuildSynthesisPrompt(string goal, IReadOnlyList<MergedAgent> merged)
    {
        var sb = new StringBuilder();
        sb.Append("Goal: ").Append(goal).Append("\n\n");

        foreach (var a in merged)
        {
            sb.Append("=== Agent ").Append(a.AgentRunId).Append(" (").Append(a.Status).Append(") ===\n");
            if (!string.IsNullOrWhiteSpace(a.Summary)) sb.Append("Summary: ").Append(a.Summary).Append('\n');
            sb.Append("Diff:\n").Append(string.IsNullOrEmpty(a.Patch) ? "(no diff captured)" : a.Patch).Append("\n\n");
        }

        return sb.ToString();
    }

    // ── Facet (a): the model-free on-disk integration ───────────────────────────────

    private async Task<object> IntegrateMergedAsync(Guid repoId, SupervisorTurnContext context, IReadOnlyList<MergedAgent> merged, CancellationToken cancellationToken)
    {
        var workspace = await _workspaces.ResolveByRepositoryIdAsync(repoId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (workspace is null) return new { status = "Skipped", reason = "the repository could not be resolved to a clone target" };

        // Only agents that recorded a base + a diff can be integrated by patch — a failed / abandoned / analysis-only
        // agent (no base) is EXCLUDED so it can't sink the whole clean set (its work stays in the side-by-side fold +
        // on its own branch). Surfaced honestly as `excludedAgents` so the outcome stays truthful about who combined.
        var eligible = merged.Where(m => !string.IsNullOrEmpty(m.BaseSha)).ToList();
        var excluded = merged.Where(m => string.IsNullOrEmpty(m.BaseSha)).Select(m => m.AgentRunId.ToString()).ToList();

        if (eligible.Count == 0) return new { status = "Skipped", reason = "no agent recorded a base revision (an analysis-only run has nothing to integrate)", excludedAgents = excluded };

        var request = BuildIntegrationRequest(repoId, context, workspace, eligible[0].BaseSha!, eligible);

        try
        {
            var result = await _integrator.IntegrateAsync(request, cancellationToken).ConfigureAwait(false);
            return ProjectIntegrationResult(result, excluded);
        }
        catch (WorkspaceException ex)
        {
            // Best-effort: a git infrastructure failure (auth / network) is recorded (token already redacted by the
            // integrator) but NEVER crashes the merge turn — the deterministic fold + the K branches remain.
            _logger.LogWarning(ex, "Supervisor branch integration failed; keeping the side-by-side fold");
            return new { status = "Failed", reason = ex.Message, excludedAgents = excluded };
        }
    }

    private static IntegrationRequest BuildIntegrationRequest(Guid repoId, SupervisorTurnContext context, WorkspaceRequest workspace, string baseSha, IReadOnlyList<MergedAgent> eligible) => new()
    {
        TeamId = context.TeamId,
        RepositoryUrl = workspace.RepositoryUrl,
        BaseRef = workspace.Ref,
        BaseSha = baseSha,
        Token = workspace.Token,
        TokenUsername = workspace.TokenUsername,
        // Per-MERGE-TURN branch (not per-run): a supervisor may merge repeatedly (spawn→merge→spawn→merge), each over a
        // strictly larger agent set → a different tree. A run-id-only name would pin the branch to wave 1 and refuse
        // every later, larger merge as "advanced". The turn discriminator gives each wave its own reviewable branch
        // while a re-executed SAME turn maps to the same branch (the tree-equality idempotence no-op still holds).
        IntegrationBranch = $"codespace/integration/{context.SupervisorRunId:N}/turn{context.TurnNumber}",
        Depth = 0,
        Contributions = eligible.Select(m => new BranchContribution
        {
            Label = m.AgentRunId.ToString(),
            SourceRepositoryId = repoId,
            BaseSha = m.BaseSha,
            Patch = m.Patch,            // already resolved in .Merge.cs (offloaded diffs folded back) → no artifact id
            ProducedBranch = m.ProducedBranch,
        }).ToList(),
    };

    private static object ProjectIntegrationResult(IntegrationResult result, IReadOnlyList<string> excludedAgents) => new
    {
        status = result.Status.ToString(),
        integratedBranch = result.IntegratedBranch,
        appliedCount = result.AppliedCount,
        reason = result.Reason,
        excludedAgents,
        outcomes = result.Outcomes.Select(o => new { label = o.Label, disposition = o.Disposition.ToString(), reason = o.Reason, conflictedFiles = o.ConflictedFiles, fallbackBranch = o.FallbackBranch }).ToList(),
    };
}
