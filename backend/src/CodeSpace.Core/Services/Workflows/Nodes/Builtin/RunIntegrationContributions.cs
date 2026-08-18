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
/// fail-closed behaviour and stay untouched. Superseded DUPLICATES are what goes — never an outcome filter, so a
/// unit whose only attempt failed but captured a diff still contributes.</para>
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
            .Select(pair => (pair.Manifest, Work: pair.Work!));

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
    private static bool CellIsTheUnit(string? taskJson)
    {
        if (string.IsNullOrWhiteSpace(taskJson)) return false;

        try
        {
            var task = JsonSerializer.Deserialize<AgentTask>(taskJson, AgentJson.Options);

            return task is not null && task.WorkUnit is null && string.IsNullOrEmpty(task.SubtaskId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The reduction keys on the ATTEMPT (the agent run), not the row — a surviving multi-repo attempt keeps every one of its own per-alias rows. Newest by agent-run creation, tie-broken on id so the pick is total and repeats across builds.</summary>
    private static IEnumerable<(PublishManifest Manifest, RunAgentWork Work)> KeepLatestAttempt(IEnumerable<(PublishManifest Manifest, RunAgentWork Work)> unit)
    {
        var latest = unit.OrderBy(pair => pair.Work.CreatedDate).ThenBy(pair => pair.Work.AgentRunId).Last().Work.AgentRunId;

        return unit.Where(pair => pair.Work.AgentRunId == latest);
    }
}
