namespace CodeSpace.Messages.Enums;

/// <summary>
/// Lifecycle of a <c>workflow_run_request</c>. Persisted as TEXT in the DB (the column
/// is <c>status</c>, validated by an app-layer CHECK we may add later — kept loose for
/// now so new states (e.g. <c>throttled</c>) don't require a migration).
///
/// Most requests march through: Received → Verified → Normalized → Matched → Consumed.
/// Rejected is the dead-end branch (signature failure, no matching activation, dedup hit).
/// </summary>
public enum WorkflowRunRequestStatus
{
    /// <summary>Row inserted; raw payload + headers landed but no processing started yet.</summary>
    Received,

    /// <summary>Signature / auth check passed (provider webhooks). N/A for trusted sources (Manual/Replay) — they skip straight past.</summary>
    Verified,

    /// <summary>Raw payload normalised to the engine-facing shape; <c>normalized_payload_json</c> populated.</summary>
    Normalized,

    /// <summary>Resolved to a concrete <c>workflow_id</c> + <c>activation_id</c>; snapshot of the activation row captured.</summary>
    Matched,

    /// <summary>A <c>workflow_run</c> row was created from this request and handed to the run dispatcher for Pending→Enqueued CAS + Hangfire pickup.</summary>
    Consumed,

    /// <summary>Request was rejected somewhere in the pipeline; <c>error</c> column holds the reason.</summary>
    Rejected
}
