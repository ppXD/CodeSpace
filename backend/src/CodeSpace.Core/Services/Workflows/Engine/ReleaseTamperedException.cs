namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Thrown when the engine's pre-execution hash check finds that the
/// <c>workflow_version.definition_json</c> bytes have drifted from the stored
/// <c>workflow_version.definition_hash</c> — i.e. someone bypassed the
/// version-immutability rule and mutated the JSON directly in the DB.
///
/// <para>The engine treats this as a fatal Failure (not a node error): the run is marked
/// <c>WorkflowRunStatus.Failure</c> with this message so the operator sees exactly which
/// version drifted + the hash diff. Existing snapshot rows for the run remain untouched
/// — the operator can inspect what the engine thought the release looked like vs the
/// current JSON. Replay safety is preserved: a tampered version is rejected at every
/// future run attempt until the hash is reconciled (either restore the original JSON or
/// publish a NEW version with the new bytes).</para>
/// </summary>
public sealed class ReleaseTamperedException : Exception
{
    public ReleaseTamperedException(Guid workflowId, int workflowVersion, string storedHash, string recomputedHash)
        : base(
            $"Release tampering detected for workflow {workflowId} version {workflowVersion}: " +
            $"stored hash = '{storedHash}', recomputed hash from current definition_json = '{recomputedHash}'. " +
            $"workflow_version rows are immutable by contract — the JSON has been mutated outside the publish " +
            $"path. Either restore the original JSON for this version, or publish a NEW version with the new " +
            $"definition bytes. The engine refuses to execute a tampered release.")
    {
        WorkflowId = workflowId;
        WorkflowVersion = workflowVersion;
        StoredHash = storedHash;
        RecomputedHash = recomputedHash;
    }

    public Guid WorkflowId { get; }
    public int WorkflowVersion { get; }
    public string StoredHash { get; }
    public string RecomputedHash { get; }
}
