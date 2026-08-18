using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
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
/// <para>Fail-closed, never fail-quiet: a MISSING offloaded artifact throws (the caller must not treat lost bytes as
/// an empty diff), while an absent/unparseable result row resolves to empty — the integrator then names that
/// contribution unintegrable rather than this layer guessing.</para>
/// </summary>
public interface IAgentPatchReader
{
    /// <summary>Resolve one producer's diff: the offloaded artifact when <see cref="AgentPatchSource.PatchArtifactId"/> is set (team-scoped, fail-closed), else the producing run's inline patch. Never both — the two carriers are mutually exclusive by construction.</summary>
    Task<string> ReadAsync(Guid teamId, AgentPatchSource source, CancellationToken cancellationToken);
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
    /// <summary>The inline diff the manifest doesn't carry: the result's top-level patch (single-repo run), else the matching per-repo entry's (multi-repo run). Empty when the result is absent/unparseable — the integrator then names the contribution unintegrable instead of this layer guessing.</summary>
    public static string From(string? resultJson, string repositoryAlias)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return "";

        try
        {
            var result = JsonSerializer.Deserialize<AgentRunResult>(resultJson, AgentJson.Options);

            if (result is null) return "";
            if (result.Patch is { Length: > 0 } patch) return patch;

            return result.RepositoryResults?.FirstOrDefault(r => string.Equals(r.Alias, repositoryAlias, StringComparison.Ordinal))?.Patch ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
