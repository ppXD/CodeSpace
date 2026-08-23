using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Retention;

/// <summary>
/// The column-complete reference check. One <c>EXISTS</c> per referencing COLUMN — not per table — so each probe lands
/// on the matching index from migration 0144 instead of an OR that a planner would turn into a scan.
///
/// <para>The site list is the enumeration of every column in the schema whose name ends in <c>artifact_id</c> and whose
/// target is <c>workflow_artifact</c>. <see cref="ReferenceSites"/> is public and pinned by a test, because a new
/// referencing column that is added to the schema and NOT added here would make the oracle answer "unreferenced" about
/// an artifact that is referenced — the one failure mode of this class that destroys data.</para>
/// </summary>
public sealed class ArtifactReferenceOracle : IArtifactReferenceOracle, IScopedDependency
{
    /// <summary>
    /// Every <c>(table, column)</c> that soft-links <c>workflow_artifact.id</c>. Public and test-pinned: this list IS
    /// the correctness of the reaper, and a column missing from it is silent data loss.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> ReferenceSites =
    [
        ("artifact_manifest", "content_artifact_id"),
        ("publish_manifest", "patch_artifact_id"),
        ("agent_run_event", "data_artifact_id"),
        ("workflow_run_model_call", "request_artifact_id"),
        ("workflow_run_model_call_attempt", "request_artifact_id"),
        ("workflow_run_model_call_attempt", "response_artifact_id"),
        ("workflow_run_model_call_attempt", "error_artifact_id"),
        ("workflow_run_model_call_body_capture", "artifact_id"),
        ("workflow_run_tool_call", "arguments_artifact_id"),
        ("workflow_run_tool_call_attempt", "result_artifact_id"),
        ("workflow_run_tool_call_attempt", "error_artifact_id"),
    ];

    private static readonly string ExistsSql = BuildExistsSql();

    private readonly ILogger<ArtifactReferenceOracle> _logger;

    public ArtifactReferenceOracle(ILogger<ArtifactReferenceOracle> logger) => _logger = logger;

    public async Task<ArtifactReferenceVerdict> ClassifyAsync(CodeSpaceDbContext db, Guid artifactId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (artifactId == Guid.Empty) return ArtifactReferenceVerdict.Indeterminate;

        try
        {
            var arguments = Enumerable.Repeat<object>(artifactId, ReferenceSites.Count).ToArray();
            var referenced = await db.Database.SqlQueryRaw<bool>(ExistsSql, arguments).SingleAsync(cancellationToken).ConfigureAwait(false);

            return referenced ? ArtifactReferenceVerdict.Referenced : ArtifactReferenceVerdict.Unreferenced;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-closed: an unreachable or unreadable reference site means the question is UNANSWERED. Reporting
            // "unreferenced" here would delete an object whose references were never actually inspected.
            _logger.LogWarning(ex, "Artifact {ArtifactId}: reference sites could not be probed; treating reference status as indeterminate", artifactId);

            return ArtifactReferenceVerdict.Indeterminate;
        }
    }

    /// <summary>
    /// One boolean per round trip: the OR of an EXISTS per site. Built from <see cref="ReferenceSites"/> so adding a
    /// site cannot be forgotten in the SQL. Each site gets its OWN positional placeholder — the caller passes the same
    /// id once per site — so the statement never depends on how EF de-duplicates a repeated placeholder index.
    /// </summary>
    private static string BuildExistsSql()
    {
        var probes = ReferenceSites.Select((site, index) => $"EXISTS (SELECT 1 FROM {site.Table} WHERE {site.Column} = {{{index}}})");

        return $"SELECT ({string.Join(" OR ", probes)}) AS \"Value\"";
    }
}
