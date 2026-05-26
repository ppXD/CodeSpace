-- 0016_workflow_artifact.sql
-- Phase 2.11 — content-addressable artifact storage.
--
-- Once the run-record ledger exists (Phase 2.10), payloads start growing: a 50 MB HTTP
-- response body, a 200 KB LLM completion, a binary artifact fetched from an external
-- system. Embedding those in workflow_run_record.payload_json bloats the ledger and
-- duplicates the content on every retry.
--
-- The artifact table is a content-addressable side-table: same SHA-256 → same row, dedup'd
-- per team. Small content (< inline threshold) lands inline as BYTEA for one-roundtrip
-- access; larger content gets a storage_url pointer for out-of-band storage (object store,
-- disk, etc.). Records reference artifacts by id in their payload_json (e.g.
-- `external_call.completed: {"response_artifact_id": "<uuid>"}`).
--
-- Tenant isolation: artifacts are scoped per team. Two teams can store identical bytes;
-- they get separate rows (no cross-team dedup) so a leak of one team's artifact doesn't
-- expose the other's existence.
--
-- Immutable after insert: a trigger rejects UPDATE/DELETE. Cleanup of orphan artifacts
-- (referenced by no record) is a future operator-controlled job.

CREATE TABLE workflow_artifact (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id         UUID NOT NULL REFERENCES team(id),

    -- SHA-256 of the raw bytes, hex-lowercase (64 chars). Combined with team_id as a
    -- unique constraint so PutAsync(team, bytes) is idempotent: storing the same content
    -- twice returns the same row id without a duplicate row.
    sha256          TEXT NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),

    -- MIME type of the content. Application-supplied; the store does not validate the
    -- bytes against the declared type — that's caller responsibility.
    content_type    TEXT NOT NULL,

    -- Total content size in bytes. Stored separately from the inline column because
    -- storage_url rows have inline_bytes=NULL but still need a size for size-budget
    -- accounting on the run-detail UI.
    size_bytes      BIGINT NOT NULL CHECK (size_bytes >= 0),

    -- Inline content for small artifacts. NULL when content is too large and lives at
    -- storage_url instead. Threshold is enforced by IArtifactStore (8 KiB by default,
    -- configurable via env var pinned by Rule 8).
    inline_bytes    BYTEA NULL,

    -- Storage system URL for large artifacts. Format: scheme://opaque (e.g.
    -- s3://bucket/key, file:///mnt/store/abc/def...). Resolution is implementation-defined
    -- by the storage backend; the store layer hands the URL back to the caller for fetch.
    storage_url     TEXT NULL,

    -- Exactly one of inline_bytes / storage_url must be set. CHECK enforces; PutAsync's
    -- threshold logic guarantees the invariant from the app side.
    CONSTRAINT workflow_artifact_storage_xor CHECK (
        (inline_bytes IS NOT NULL AND storage_url IS NULL) OR
        (inline_bytes IS NULL AND storage_url IS NOT NULL)
    ),

    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Per-team dedup. Same bytes from the same team always resolve to the same row;
    -- cross-team identical bytes get distinct rows (intentional — see header note).
    UNIQUE (team_id, sha256)
);

-- ─── Indexes ──────────────────────────────────────────────────────────────────

-- Per-team listing (admin "show me everything" view, future cleanup job scans).
CREATE INDEX idx_workflow_artifact_team ON workflow_artifact(team_id, created_at DESC);

-- ─── Append-only immutability trigger ──────────────────────────────────────────
-- Artifacts are immutable: the SHA is the identity, mutating bytes would silently corrupt
-- every reference. UPDATE is rejected outright. DELETE is rejected by default but may be
-- bypassed by an operator-controlled cleanup job that sets a session variable
-- (`SET LOCAL codespace.artifact_purge_allowed = on`) — out of scope for Phase 2.11; the
-- pattern just keeps the door open without compromising default safety.

CREATE OR REPLACE FUNCTION workflow_artifact_reject_mutations() RETURNS TRIGGER AS $$
DECLARE
    purge_allowed TEXT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        -- Session-level escape hatch for future cleanup jobs. current_setting with the
        -- 'missing_ok' second arg returns NULL when the var isn't set, so the default path
        -- is rejection.
        purge_allowed := current_setting('codespace.artifact_purge_allowed', true);
        IF purge_allowed = 'on' THEN
            RETURN OLD;
        END IF;
    END IF;

    RAISE EXCEPTION
        'workflow_artifact is immutable — % rejected (id=%, sha256=%). '
        'For DELETE, set codespace.artifact_purge_allowed=on in the same session as a privileged operation.',
        TG_OP, OLD.id, OLD.sha256;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_artifact_enforce_immutability
    BEFORE UPDATE OR DELETE ON workflow_artifact
    FOR EACH ROW EXECUTE FUNCTION workflow_artifact_reject_mutations();

COMMENT ON TABLE workflow_artifact IS
    'Phase 2.11 — content-addressable artifact storage. Workflow_run_record entries reference '
    'artifact ids in their payload_json instead of embedding large bytes. Per-team dedup by '
    'sha256. Immutable after insert (trigger). Inline storage for small payloads, storage_url '
    'pointer for large ones.';
