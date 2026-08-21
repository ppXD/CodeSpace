using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish.Exceptions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Publish;

/// <summary>
/// Compact, observation-only projection for the dependency-staging empty-carrier guard. The required patch reader
/// remains the only byte authority; this query answers only whether the manifest-selected INLINE carrier is nonempty.
/// PostgreSQL performs alias selection against the normalized <c>result_jsonb</c> and returns a boolean or the same
/// typed structural refusal as <see cref="AgentInlinePatch.From"/>. No patch/result body crosses the DB boundary.
/// </summary>
internal static class AgentInlinePatchObservation
{
    private const int Empty = 0;
    private const int HasInlinePatch = 1;
    private const int AliasMissing = 2;
    private const int AliasAmbiguous = 3;
    private const int UnexpectedArtifact = 4;

    public static async Task<IReadOnlyList<bool>> ReadAsync(CodeSpaceDbContext db, Guid teamId, IReadOnlyList<AgentPatchSource> sources, int maxSources, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSources, 1);
        if (sources.Count == 0) return [];
        if (sources.Any(source => source.PatchArtifactId is not null))
            throw new ArgumentException("Inline patch observation accepts only sources whose PatchArtifactId is null.", nameof(sources));

        var keys = sources.Select(SourceKey.From).Distinct().ToList();
        if (keys.Count > maxSources)
            throw new ArgumentOutOfRangeException(nameof(sources), keys.Count, $"At most {maxSources} distinct inline patch sources may be observed in one dependency-staging batch.");

        var queryKeys = keys.Where(key => key.AgentRunId is not null).ToList();
        if (queryKeys.Count == 0) return Enumerable.Repeat(false, sources.Count).ToList();

        var agentRunIds = queryKeys.Select(key => key.AgentRunId!.Value).ToArray();
        var repositoryAliases = queryKeys.Select(key => key.RepositoryAlias).ToArray();
        var statusNames = Enum.GetNames<AgentRunStatus>();
        var rows = await Query(db, teamId, agentRunIds, repositoryAliases, statusNames).ToListAsync(cancellationToken).ConfigureAwait(false);
        var bySource = rows.ToDictionary(row => new SourceKey(row.AgentRunId, row.RepositoryAlias));

        var result = new List<bool>(sources.Count);
        foreach (var source in sources)
        {
            var key = SourceKey.From(source);
            if (key.AgentRunId is null || !bySource.TryGetValue(key, out var row))
            {
                result.Add(false);
                continue;
            }

            result.Add(Resolve(row));
        }

        return result;
    }

    /// <summary>
    /// One fixed-parameter query for K exact (AgentRunId, repositoryAlias) coordinates. The result contract persisted
    /// by AgentRunService is canonical camelCase JSON; shape checks conservatively classify a malformed carrier as
    /// empty, matching AgentInlinePatch's JsonException behavior. Repository aliases remain ordinal/case-sensitive.
    /// </summary>
    internal static IQueryable<ObservationRow> Query(CodeSpaceDbContext db, Guid teamId, Guid[] agentRunIds, string[] repositoryAliases, string[] statusNames) =>
        db.Database.SqlQuery<ObservationRow>($$"""
            WITH requested AS (
                SELECT request.agent_run_id, request.repository_alias
                FROM unnest({{agentRunIds}}::uuid[], {{repositoryAliases}}::text[]) AS request(agent_run_id, repository_alias)
            ), documents AS (
                SELECT request.agent_run_id,
                       request.repository_alias,
                       run.result_jsonb,
                       CASE WHEN jsonb_typeof(run.result_jsonb -> 'repositoryResults') = 'array'
                            THEN jsonb_array_length(run.result_jsonb -> 'repositoryResults')
                            ELSE 0 END AS repository_count,
                       CASE WHEN run.id IS NOT NULL
                                  AND jsonb_typeof(run.result_jsonb) = 'object'
                                  AND jsonb_typeof(run.result_jsonb -> 'status') = 'string'
                                  AND run.result_jsonb ->> 'status' = ANY({{statusNames}}::text[])
                                  AND run.result_jsonb ? 'exitReason'
                                  AND (run.result_jsonb -> 'exitReason' = 'null'::jsonb OR jsonb_typeof(run.result_jsonb -> 'exitReason') = 'string')
                                  AND (NOT run.result_jsonb ? 'repositoryResults'
                                       OR run.result_jsonb -> 'repositoryResults' = 'null'::jsonb
                                       OR jsonb_typeof(run.result_jsonb -> 'repositoryResults') = 'array')
                            THEN TRUE ELSE FALSE END AS result_shape_valid
                FROM requested AS request
                LEFT JOIN agent_run AS run ON run.id = request.agent_run_id AND run.team_id = {{teamId}}
            ), selected AS (
                SELECT document.agent_run_id,
                       document.repository_alias,
                       document.result_jsonb,
                       document.repository_count,
                       document.result_shape_valid,
                       repository.match_count,
                       repository.shape_valid AS repository_shape_valid,
                       repository.patch_nonempty,
                       repository.artifact_id
                FROM documents AS document
                LEFT JOIN LATERAL (
                    SELECT count(*) FILTER (WHERE jsonb_typeof(item.value) = 'object' AND item.value ->> 'alias' = document.repository_alias) AS match_count,
                           bool_and(item.value = 'null'::jsonb OR (
                               jsonb_typeof(item.value) = 'object'
                               AND jsonb_typeof(item.value -> 'alias') = 'string'
                               AND (NOT item.value ? 'patch' OR item.value -> 'patch' = 'null'::jsonb OR jsonb_typeof(item.value -> 'patch') = 'string')
                               AND (NOT item.value ? 'patchArtifactId' OR item.value -> 'patchArtifactId' = 'null'::jsonb
                                    OR (jsonb_typeof(item.value -> 'patchArtifactId') = 'string'
                                        AND item.value ->> 'patchArtifactId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'))
                           )) AS shape_valid,
                           bool_or(jsonb_typeof(item.value) = 'object'
                               AND item.value ->> 'alias' = document.repository_alias
                               AND jsonb_typeof(item.value -> 'patch') = 'string'
                               AND item.value ->> 'patch' ~ '[^[:space:]]') AS patch_nonempty,
                           max(CASE WHEN jsonb_typeof(item.value) = 'object'
                                         AND item.value ->> 'alias' = document.repository_alias
                                         AND jsonb_typeof(item.value -> 'patchArtifactId') = 'string'
                                         AND item.value ->> 'patchArtifactId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                                    THEN item.value ->> 'patchArtifactId' END)::uuid AS artifact_id
                    FROM jsonb_array_elements(CASE WHEN document.repository_count > 0
                                                   THEN document.result_jsonb -> 'repositoryResults'
                                                   ELSE '[]'::jsonb END) AS item(value)
                ) AS repository ON TRUE
            ), resolved AS (
                SELECT selected.agent_run_id,
                       selected.repository_alias,
                       CASE
                           WHEN NOT selected.result_shape_valid THEN {{Empty}}
                           WHEN selected.repository_count > 0 AND NOT COALESCE(selected.repository_shape_valid, FALSE) THEN {{Empty}}
                           WHEN selected.repository_count > 0 AND selected.match_count = 0 THEN {{AliasMissing}}
                           WHEN selected.repository_count > 0 AND selected.match_count > 1 THEN {{AliasAmbiguous}}
                           WHEN selected.repository_count > 0 AND selected.artifact_id IS NOT NULL THEN {{UnexpectedArtifact}}
                           WHEN selected.repository_count > 0 AND COALESCE(selected.patch_nonempty, FALSE) THEN {{HasInlinePatch}}
                           WHEN selected.repository_count > 0 THEN {{Empty}}
                           WHEN jsonb_typeof(selected.result_jsonb -> 'patchArtifactId') = 'string'
                                AND selected.result_jsonb ->> 'patchArtifactId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN {{UnexpectedArtifact}}
                           WHEN selected.result_jsonb ? 'patchArtifactId'
                                AND selected.result_jsonb -> 'patchArtifactId' <> 'null'::jsonb THEN {{Empty}}
                           WHEN selected.result_jsonb ? 'patch'
                                AND selected.result_jsonb -> 'patch' <> 'null'::jsonb
                                AND jsonb_typeof(selected.result_jsonb -> 'patch') <> 'string' THEN {{Empty}}
                           WHEN jsonb_typeof(selected.result_jsonb -> 'patch') = 'string'
                                AND selected.result_jsonb ->> 'patch' ~ '[^[:space:]]' THEN {{HasInlinePatch}}
                           ELSE {{Empty}}
                       END AS resolution,
                       CASE WHEN selected.repository_count > 0 THEN selected.artifact_id
                            WHEN jsonb_typeof(selected.result_jsonb -> 'patchArtifactId') = 'string'
                                 AND selected.result_jsonb ->> 'patchArtifactId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                                THEN (selected.result_jsonb ->> 'patchArtifactId')::uuid END AS artifact_id
                FROM selected
            )
            SELECT resolved.agent_run_id,
                   resolved.repository_alias,
                   resolved.resolution,
                   resolved.artifact_id,
                   resolved.resolution = {{HasInlinePatch}} AS has_inline_patch
            FROM resolved
            """);

    private static bool Resolve(ObservationRow row) => (row.Resolution, row.HasInlinePatch) switch
    {
        (Empty, false) => false,
        (HasInlinePatch, true) => true,
        (AliasMissing, false) => throw new AgentInlinePatchResolutionException(row.RepositoryAlias, AgentInlinePatchResolutionKind.RepositoryAliasMissing),
        (AliasAmbiguous, false) => throw new AgentInlinePatchResolutionException(row.RepositoryAlias, AgentInlinePatchResolutionKind.RepositoryAliasAmbiguous),
        (UnexpectedArtifact, false) => throw new AgentInlinePatchResolutionException(row.RepositoryAlias, AgentInlinePatchResolutionKind.UnexpectedArtifactReference, row.ArtifactId),
        _ => throw new InvalidOperationException($"Unknown inline patch observation resolution '{row.Resolution}'."),
    };

    private sealed record SourceKey(Guid? AgentRunId, string RepositoryAlias)
    {
        public static SourceKey From(AgentPatchSource source) => new(source.AgentRunId, source.RepositoryAlias);
    }

    internal sealed class ObservationRow
    {
        public Guid AgentRunId { get; set; }
        public string RepositoryAlias { get; set; } = default!;
        public int Resolution { get; set; }
        public Guid? ArtifactId { get; set; }
        public bool HasInlinePatch { get; set; }
    }
}
