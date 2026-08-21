-- 0153_workflow_run_native_record_source_end.sql
--
-- Preserve the durable stdout reader's exact half-open source byte range on every new native record. The original
-- source_length_bytes remains the raw frame CONTENT length (and therefore still differs from captured size under
-- redaction); source_end_offset_bytes additionally includes the source's real LF/CRLF terminator, or no terminator for
-- an unterminated final frame / bounded continuation. It is nullable solely for rows written before this migration.
--
-- A re-attach compares its true spool checkpoint with the record plane's true end. Legacy rows retain the old
-- reconstruction (content length plus one for a final frame) at read time; rewriting append-only historical records
-- would invent geometry the old reader had already discarded.
-- Rollback: ALTER TABLE workflow_run_native_record DROP COLUMN source_end_offset_bytes; restore the old bounds check.

ALTER TABLE workflow_run_native_record
    ADD COLUMN source_end_offset_bytes BIGINT NULL;

ALTER TABLE workflow_run_native_record
    DROP CONSTRAINT ck_workflow_run_native_record_bounds,
    ADD CONSTRAINT ck_workflow_run_native_record_bounds CHECK (
        ordinal >= 0 AND source_offset_bytes >= 0 AND source_length_bytes >= 0
        AND (source_end_offset_bytes IS NULL OR source_end_offset_bytes >= source_offset_bytes + source_length_bytes)
        AND size_bytes >= 0 AND contract_version > 0 AND btrim(native_type) <> ''
        AND (native_schema IS NULL OR btrim(native_schema) <> '')
        AND (native_schema_version IS NULL OR btrim(native_schema_version) <> ''));

COMMENT ON COLUMN workflow_run_native_record.source_offset_bytes IS
    'Exact start of the raw frame in source bytes for records with source_end_offset_bytes; legacy reconstructed cursor otherwise.';
COMMENT ON COLUMN workflow_run_native_record.source_length_bytes IS
    'Byte length of the raw frame content after the reader removes its source terminator; independent of captured size under redaction.';
COMMENT ON COLUMN workflow_run_native_record.source_end_offset_bytes IS
    'Exact exclusive source byte end, including the real LF/CRLF terminator when present. NULL only for legacy records whose reader did not preserve that fact.';
