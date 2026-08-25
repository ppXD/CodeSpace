namespace CodeSpace.Messages.Constants;

/// <summary>
/// Wire keys inside a <c>workflow_run_record.payload_json</c> object that carry MEANING BEYOND the value they
/// sit next to — a reader branches on them, so renaming one is a ledger schema break exactly like renaming a
/// <c>record_type</c>. Pinned by <c>WorkflowRunRecordPayloadKeysTests</c> so a rename is a compile-error-visible
/// decision rather than a silent break of every row already written under the old name.
/// </summary>
public static class WorkflowRunRecordPayloadKeys
{
    /// <summary>
    /// On a <c>node.completed</c> record: the <c>outputs</c> in THIS row are the REDACTED copy, and the originals
    /// were meant to land in the encrypted same-record sidecar. Present only when redaction actually replaced
    /// something, so its ABSENCE means "this row's outputs are the real values" — the reading that every row
    /// written before this key existed, and every row with nothing to redact, must keep.
    ///
    /// <para>It exists because sidecar PRESENCE alone cannot separate "no sidecar was ever owed" from "a sidecar
    /// was owed and never committed". Without the key a reader that found no sidecar silently handed the
    /// redaction marker downstream as if it were the secret.</para>
    /// </summary>
    public const string OutputsRedacted = "outputsRedacted";
}
