-- 0111_capture_intent.sql
--
-- P2 (durable capture, slice 1): the CAPTURE INTENT saga row — a durable promise written when an agent's harness
-- exits, BEFORE any capture side effect (diff, artifact offload, branch push, manifest row), and committed only
-- after the capture sequence persisted its facts. Today every capture step is individually best-effort-swallowed
-- and the crash-recovery spool path terminalizes a run with NO capture at all — so "Succeeded with silently lost
-- artifacts" is reachable through seven distinct catch sites and one crash window, and a legitimately-empty
-- capture is indistinguishable from a swallowed failure. The intent row splits those states: Committed (the
-- capture sequence ran to its persist — including a CONFIRMED empty), Indeterminate (the attempt died mid-window;
-- the work may or may not exist — visible, never silent), Intended (still in flight).
--
-- One row per ATTEMPT: (agent_run_id, fence_epoch) is unique — a reclaimed re-attach runs at a bumped epoch and
-- gets its own promise. expectations_jsonb carries the intent-time facts (repo cardinality, the durable source
-- handle); facts_jsonb carries the commit-time observation. Soft links (no FK) to workflow_run, mirroring
-- completion_receipt.
--
-- Rollback: DROP TABLE capture_intent. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS capture_intent (
    id                  UUID        NOT NULL PRIMARY KEY,
    team_id             UUID        NOT NULL REFERENCES team(id),
    agent_run_id        UUID        NOT NULL,
    workflow_run_id     UUID        NULL,
    fence_epoch         BIGINT      NOT NULL,
    status              VARCHAR(20) NOT NULL,
    expectations_jsonb  JSONB       NULL,
    facts_jsonb         JSONB       NULL,
    created_date        TIMESTAMPTZ NOT NULL,
    created_by          UUID        NOT NULL,
    last_modified_date  TIMESTAMPTZ NOT NULL,
    last_modified_by    UUID        NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_capture_intent_attempt ON capture_intent (agent_run_id, fence_epoch);
CREATE INDEX IF NOT EXISTS ix_capture_intent_team ON capture_intent (team_id);
CREATE INDEX IF NOT EXISTS ix_capture_intent_status ON capture_intent (status) WHERE status = 'Intended';
