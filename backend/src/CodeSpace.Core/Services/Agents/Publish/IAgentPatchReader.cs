using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Publish;

/// <summary>
/// The ONE place that answers "what diff bytes did this producer capture" for a consumer holding a
/// <see cref="Persistence.Entities.PublishManifest"/> row — supervisor dependency staging and the
/// <c>git.integrate_run</c> node — so the two cannot drift on the answer. They did: the node had the inline
/// fallback and staging did not, which made every sub-threshold diff read patch-less and block the whole handoff.
///
/// <para>The size gate is the reason two carriers exist at all: <c>ArtifactOffloader.OffloadIfLargeAsync</c> returns
/// no artifact id at or below <c>ArtifactStoreConfig.InlineThresholdBytes</c>, so a small diff is recorded ONLY in
/// <c>agent_run.result_jsonb</c> while a large one is recorded ONLY in the artifact store. The manifest row names the
/// second and cannot name the first — reading the manifest alone is therefore never sufficient.</para>
///
/// <para>The supervisor <c>merge</c> deliberately stays off this seam: it already holds each agent's deserialized
/// result and resolves the top-level diff plus every per-repo diff through the SAME offloader primitive
/// (<c>.Merge.cs</c>). Alias SELECTION is what a manifest-driven consumer needs and a whole-result consumer does not.</para>
///
/// <para>Fail-closed, never fail-quiet: a MISSING offloaded artifact, a multi-repo alias mismatch, or a result/manifest
/// carrier mismatch throws (the caller must not treat lost or misbound bytes as an empty diff). An absent/unparseable
/// result row still resolves to empty — the integrator then names that contribution unintegrable rather than this
/// layer guessing.</para>
/// </summary>
public interface IAgentPatchReader
{
    /// <summary>Resolve one producer's diff: the offloaded artifact when <see cref="AgentPatchSource.PatchArtifactId"/> is set (team-scoped, fail-closed), else the producing run's inline patch. Never both — the two carriers are mutually exclusive by construction.</summary>
    Task<string> ReadAsync(Guid teamId, AgentPatchSource source, CancellationToken cancellationToken);

    /// <summary>Observation-only batch used by the staging empty-carrier guard. Returns one boolean per input, in input order, without materializing result documents or patch bytes. Every source must name the inline carrier (no artifact id).</summary>
    Task<IReadOnlyList<bool>> HasInlinePatchesAsync(Guid teamId, IReadOnlyList<AgentPatchSource> sources, int maxSources, CancellationToken cancellationToken);
}

public sealed class AgentPatchReader : IAgentPatchReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactOffloader _offloader;

    public AgentPatchReader(CodeSpaceDbContext db, IArtifactOffloader offloader)
    {
        _db = db;
        _offloader = offloader;
    }

    public async Task<string> ReadAsync(Guid teamId, AgentPatchSource source, CancellationToken cancellationToken)
    {
        if (source.PatchArtifactId is { } artifactId)
            return await _offloader.ResolveRequiredAsync(teamId, "", artifactId, cancellationToken).ConfigureAwait(false);

        var resultJson = await ReadResultJsonAsync(teamId, source.AgentRunId, cancellationToken).ConfigureAwait(false);

        return AgentInlinePatch.From(resultJson, source.RepositoryAlias);
    }

    public Task<IReadOnlyList<bool>> HasInlinePatchesAsync(Guid teamId, IReadOnlyList<AgentPatchSource> sources, int maxSources, CancellationToken cancellationToken) =>
        AgentInlinePatchObservation.ReadAsync(_db, teamId, sources, maxSources, cancellationToken);

    /// <summary>The producing run's recorded terminal result, TEAM-SCOPED (defense in depth, mirroring the merge read) — a cross-team or absent run resolves to nothing rather than another team's diff.</summary>
    private async Task<string?> ReadResultJsonAsync(Guid teamId, Guid? agentRunId, CancellationToken cancellationToken)
    {
        if (agentRunId is not { } runId) return null;

        return await _db.AgentRun.AsNoTracking()
            .Where(r => r.Id == runId && r.TeamId == teamId)
            .Select(r => r.ResultJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>The PURE half of <see cref="IAgentPatchReader"/> — the inline-carrier rule on its own, so the callers that already hold a result row (the run-sourced contribution mapping) apply the identical rule without a database.</summary>
public static class AgentInlinePatch
{
    /// <summary>The inline diff the manifest doesn't carry: exact alias first whenever per-repository results exist; top-level fallback only for a legacy/single-repo result. Structural alias/carrier mismatches fail closed; an absent/unparseable result remains empty so the integrator can name it unintegrable.</summary>
    public static string From(string? resultJson, string repositoryAlias)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return "";

        try
        {
            var result = JsonSerializer.Deserialize<AgentRunResult>(resultJson, AgentJson.Options);

            if (result is null) return "";
            if (result.RepositoryResults is not { Count: > 0 } repositoryResults)
                return InlineCarrier(result.Patch, result.PatchArtifactId, repositoryAlias);

            RepositoryRunResult? match = null;
            foreach (var repository in repositoryResults)
            {
                if (repository is null || !string.Equals(repository.Alias, repositoryAlias, StringComparison.Ordinal)) continue;
                if (match is not null)
                    throw new AgentInlinePatchResolutionException(repositoryAlias, AgentInlinePatchResolutionKind.RepositoryAliasAmbiguous);

                match = repository;
            }

            if (match is null)
                throw new AgentInlinePatchResolutionException(repositoryAlias, AgentInlinePatchResolutionKind.RepositoryAliasMissing);

            return InlineCarrier(match.Patch, match.PatchArtifactId, repositoryAlias);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string InlineCarrier(string? patch, Guid? artifactId, string repositoryAlias)
    {
        if (artifactId is { } id)
            throw new AgentInlinePatchResolutionException(repositoryAlias, AgentInlinePatchResolutionKind.UnexpectedArtifactReference, id);

        return patch ?? "";
    }
}
