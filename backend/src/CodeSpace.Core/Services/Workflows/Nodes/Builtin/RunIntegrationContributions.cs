using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>One agent-run row's identity + result bytes, as the contribution source needs them — a projection of <see cref="AgentRun"/>, so the pure mapping pins without a database.</summary>
public sealed record RunAgentWork(Guid AgentRunId, string? NodeId, string? IterationKey, DateTimeOffset CreatedDate, string? ResultJson);

/// <summary>
/// P4 (plan-map integrated candidate): WHICH of a run's produced work integrates, derived from the run's OWN
/// durable ledgers — the per-agent <see cref="PublishManifest"/> rows name the artifacts (base, branch, offloaded
/// patch id), and the agent-run result carries the small INLINE patch the manifest deliberately doesn't (a
/// sub-threshold diff has no artifact row — without this fallback every small-patch item would read
/// patch-less and block a clean integration). One repository per call; ordered by agent-run creation so the
/// apply order — and therefore the integration outcome — is deterministic across re-runs. Pure.
/// </summary>
public static class RunIntegrationContributions
{
    public static IReadOnlyList<BranchContribution> Build(Guid repositoryId, IReadOnlyList<PublishManifest> manifests, IReadOnlyList<RunAgentWork> agentWork)
    {
        var workByRunId = agentWork.ToDictionary(w => w.AgentRunId);

        return manifests
            .Where(m => m.Kind == PublishManifestKind.Agent && m.AgentRunId is not null && m.RepositoryId == repositoryId && m.PublishStateValue != PublishState.None)
            .Select(m => (Manifest: m, Work: workByRunId.GetValueOrDefault(m.AgentRunId!.Value)))
            .Where(pair => pair.Work is not null)
            .OrderBy(pair => pair.Work!.CreatedDate).ThenBy(pair => pair.Work!.AgentRunId)
            .Select(pair => new BranchContribution
            {
                Label = AgentAcceptanceContract.UnitId(pair.Work!.NodeId, pair.Work.IterationKey ?? ""),
                BaseSha = pair.Manifest.BaseSha,
                Patch = pair.Manifest.PatchArtifactId is null ? AgentInlinePatch.From(pair.Work.ResultJson, pair.Manifest.RepositoryAlias) : "",
                PatchArtifactId = pair.Manifest.PatchArtifactId,
                ProducedBranch = pair.Manifest.Branch,
                SourceRepositoryId = repositoryId,
            })
            .ToList();
    }
}
