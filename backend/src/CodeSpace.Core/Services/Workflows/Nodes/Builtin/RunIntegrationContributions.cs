using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>One agent-run row's identity + result bytes + task envelope, as the contribution source needs them — a projection of <see cref="AgentRun"/>, so the pure mapping pins without a database.</summary>
public sealed record RunAgentWork(Guid AgentRunId, string? NodeId, string? IterationKey, DateTimeOffset CreatedDate, string? ResultJson, string? TaskJson);

/// <summary>
/// P4 (plan-map integrated candidate): WHICH of a run's produced work integrates, derived from the run's OWN
/// durable ledgers — the per-agent <see cref="PublishManifest"/> rows name the artifacts (base, branch, offloaded
/// patch id), and the agent-run result carries the small INLINE patch the manifest deliberately doesn't (a
/// sub-threshold diff has no artifact row — without this fallback every small-patch item would read
/// patch-less and block a clean integration). One repository per call; ordered by agent-run creation so the
/// apply order — and therefore the integration outcome — is deterministic across re-runs. Pure.
///
/// <para>ONE attempt per unit, IN THE LANE WHERE THE CELL IS THE UNIT. A map body's agent node carries a retry
/// policy and every retry RESPAWNS a fresh agent run, while a manifest row is written regardless of how the owning
/// attempt ended — so a retried subtask leaves a row per attempt, and feeding all of them to a single sequential
/// apply made the unit conflict with ITSELF and parked the run on a conflict it manufactured. The abandoned
/// attempts are dropped here, at the INPUT: the integrator's sequential apply and set-level abort are correct
/// fail-closed behaviour and stay untouched. Superseded DUPLICATES are what the REDUCTION drops — it is not an
/// outcome filter, so a unit whose attempt merely ENDED badly (crashed, timed out, self-reported failure) but
/// captured a diff still contributes. The verdicts that withhold work — a rejected definition-of-done, and an output
/// review that did not approve the work (which a configured review's failed attempt never got) — are a separate,
/// explicit gate: <see cref="IsWithheldFromHead"/>.</para>
///
/// <para>The LANE FENCE is load-bearing, because the (node, iteration) cell is not a unit everywhere. A supervisor
/// stamps ONE turn cell (<c>&lt;nodeId&gt;#turn{N}</c>) on every agent it spawns in that turn, so those K rows are
/// concurrent DELIVERABLES, not attempts of one another — reducing them would silently drop K-1 real, unsuperseded
/// contributions, which is strictly worse than the self-conflict this reduction exists to remove. A row carrying
/// the supervisor's own per-agent stamp therefore skips the reduction entirely and contributes exactly as it did
/// before this reduction existed; the supervisor lane keeps resolving its own supersedes on its own keys. This is
/// the fence <c>CompletionAssessmentComposer</c> already applies before it treats the cell as a unit, widened from
/// <c>WorkUnit</c> to the <c>SubtaskId</c> that carries it, because the plan-lineage stamp is only written when a
/// plan decision exists while a plan-less supervisor spawn shares the same turn cell just the same. An unreadable
/// task envelope takes the same conservative branch — unknown lane ⇒ no reduction, since an extra integrator
/// conflict is a routable outcome and losing produced work is not.</para>
///
/// <para>SCOPE: the reduction runs after the repository filter, so the invariant is one attempt per
/// (unit, repository), not per unit. A multi-repo unit whose abandoned attempt wrote repo B while its surviving
/// attempt wrote only repo A still integrates the abandoned attempt's repo-B bytes — deliberately: repo B holds no
/// duplicate to self-conflict with, and dropping that row would discard the only produced work that repository has.</para>
/// </summary>
public static class RunIntegrationContributions
{
    public static IReadOnlyList<BranchContribution> Build(Guid repositoryId, IReadOnlyList<PublishManifest> manifests, IReadOnlyList<RunAgentWork> agentWork)
    {
        var workByRunId = agentWork.ToDictionary(w => w.AgentRunId);

        var produced = manifests
            .Where(m => m.Kind == PublishManifestKind.Agent && m.AgentRunId is not null && m.RepositoryId == repositoryId && m.PublishStateValue != PublishState.None)
            .Select(m => (Manifest: m, Work: workByRunId.GetValueOrDefault(m.AgentRunId!.Value)))
            .Where(pair => pair.Work is not null)
            .Select(pair => (pair.Manifest, Work: pair.Work!))
            .Where(pair => !IsWithheldFromHead(pair.Manifest, pair.Work));

        return LatestAttemptPerUnit(produced)
            .OrderBy(pair => pair.Work.CreatedDate).ThenBy(pair => pair.Work.AgentRunId)
            .Select(pair => new BranchContribution
            {
                Label = AgentAcceptanceContract.UnitId(pair.Work.NodeId, pair.Work.IterationKey ?? ""),
                BaseSha = pair.Manifest.BaseSha,
                Patch = pair.Manifest.PatchArtifactId is null ? AgentInlinePatch.From(pair.Work.ResultJson, pair.Manifest.RepositoryAlias) : "",
                PatchArtifactId = pair.Manifest.PatchArtifactId,
                ProducedBranch = pair.Manifest.Branch,
                SourceRepositoryId = repositoryId,
            })
            .ToList();
    }

    /// <summary>
    /// "This unit's work is WITHHELD from the reviewable head" — the ledger-row analogue of the supervisor lane's
    /// <c>SupervisorOutcome.IsWithheldFromHead</c> (its per-unit grade rejected it, or a human WAIVED its
    /// verification), read off the verdict the executor already stamps on the row
    /// (<c>AgentRunExecutor.BuildManifestUpsert</c>: passed ⇒ Passed, failed ⇒ Failed, ungraded ⇒ NotApplicable;
    /// a supervisor unit's rows are stamped later by the fold's write-back seam).
    ///
    /// <para>The deep lane has always enforced this at every door to the head (merge, resolver, publish gate). The
    /// map lane could not reach the question: its <c>flow.map</c> ran terminate-on-error, so a run containing a
    /// flunked unit failed the map and never ran the integrate step at all. Under continue-on-error it does run —
    /// and without this gate the FIRST thing the newly-reachable path would do is put work its own oracle rejected
    /// onto the one branch a human reviews as the run's candidate. Ungraded work (NotApplicable — no per-item
    /// contract, the dominant case) integrates exactly as before.</para>
    ///
    /// <para>SCOPE: an INFRA-classified failure is withheld too, because the row records only the tri-state verdict —
    /// the same coarseness the supervisor's own door accepts. Distinguishing "the check could not run" from "the
    /// check ran and said no" would need a new column on the manifest; deliberately out of scope here.</para>
    ///
    /// <para>The OUTPUT review is the other half, and the row cannot carry it: the executor upserts the manifest
    /// before the review runs, so a branch the reviewer flagged — or one its configured review never examined — still
    /// reads Pushed with an acceptance verdict of its own. The verdict is on the run's result instead, which this
    /// source already holds, so the unit is withheld through the SAME predicate the supervisor's doors read
    /// (<see cref="AgentOutputReviewHold"/>) — in its stricter form, because this source also holds the TASK: under a
    /// configured review only an approval lets a pushed branch through. A result that says nothing is no approval —
    /// none at all (the worker died after the push and the run was abandoned), or a fresh Failed one an executor fault
    /// wrote over the review's verdict while this row still names the branch. With no review configured it integrates
    /// exactly as before.</para>
    /// </summary>
    private static bool IsWithheldFromHead(PublishManifest manifest, RunAgentWork work) =>
        manifest.AcceptanceState is PublishAcceptanceState.Failed or PublishAcceptanceState.Waived || OutputReviewWithholds(work);

    /// <summary>Whether the run's configured output review did not approve its work: its result says so, or says nothing at all.</summary>
    private static bool OutputReviewWithholds(RunAgentWork work) =>
        AgentOutputReviewHold.Withholds(TryRead<AgentRunResult>(work.ResultJson), TryRead<AgentTask>(work.TaskJson)?.OutputReviewMode ?? Messages.Enums.ReviewMode.None);

    /// <summary>The stored JSON as <typeparamref name="T"/>, or null when it is absent or unreadable.</summary>
    private static T? TryRead<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Keep each unit's unsuperseded attempt, but ONLY in the lane where the (node, iteration) cell is the unit — a lane whose cell is a fan-out container (the supervisor's turn) passes through whole, so its K concurrent siblings all contribute. The caller re-orders, so the two lanes concatenate in any order.</summary>
    private static IEnumerable<(PublishManifest Manifest, RunAgentWork Work)> LatestAttemptPerUnit(IEnumerable<(PublishManifest Manifest, RunAgentWork Work)> produced)
    {
        var byLane = produced.ToLookup(pair => CellIsTheUnit(pair.Work.TaskJson));

        var reduced = byLane[true]
            .GroupBy(pair => AgentAcceptanceContract.UnitId(pair.Work.NodeId, pair.Work.IterationKey ?? ""))
            .SelectMany(KeepLatestAttempt);

        return byLane[false].Concat(reduced);
    }

    /// <summary>Whether this row's (node, iteration) cell identifies ONE unit whose rows are attempts of each other. False for a supervisor-staked row — its cell is a whole turn shared by that turn's K agents, so the per-agent stamp (<c>WorkUnit</c>, or the <c>SubtaskId</c> that carries it on a plan-less spawn) is the unit, not the cell. False too when the envelope can't be read: an unknown lane must never be reduced.</summary>
    private static bool CellIsTheUnit(string? taskJson) =>
        TryRead<AgentTask>(taskJson) is { } task && task.WorkUnit is null && string.IsNullOrEmpty(task.SubtaskId);

    /// <summary>The reduction keys on the ATTEMPT (the agent run), not the row — a surviving multi-repo attempt keeps every one of its own per-alias rows. Newest by agent-run creation, tie-broken on id so the pick is total and repeats across builds.</summary>
    private static IEnumerable<(PublishManifest Manifest, RunAgentWork Work)> KeepLatestAttempt(IEnumerable<(PublishManifest Manifest, RunAgentWork Work)> unit)
    {
        var latest = unit.OrderBy(pair => pair.Work.CreatedDate).ThenBy(pair => pair.Work.AgentRunId).Last().Work.AgentRunId;

        return unit.Where(pair => pair.Work.AgentRunId == latest);
    }
}
