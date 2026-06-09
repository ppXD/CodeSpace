-- 0040_agent_run_event.sql
--
-- Append-only event log for an agent run (B0.3) — the durable, ordered, replayable record behind
-- the live "watch exactly what the agent is doing" stream. One row per normalized AgentEvent the
-- harness emitted (kind + human-readable line + optional structured payload), stamped with a
-- globally-monotonic sequence + timestamp on persist. Mirrors workflow_run_record (the workflow
-- engine's ledger): same BIGSERIAL-cursor + run-scoped-index + append-only-trigger mechanics.
--
-- Why a global BIGSERIAL sequence: a live consumer streams ONE run with
--   WHERE agent_run_id = $1 AND sequence > $cursor ORDER BY sequence   (the idx_are_run_sequence index)
-- and a global tail consumer can use
--   WHERE sequence > $cursor ORDER BY sequence                          (across all runs)
-- — same cursor type, both monotonic, no per-run counter to coordinate.
--
-- `kind` is the NORMALIZED AgentEventKind name (closed vocabulary; the harness maps its native
-- stream into it, unknown → Warning), NOT an open string — that normalization is the whole point
-- of the harness contract, and it's what lets the UI read one vocabulary across every harness.
-- `data_json` is the optional structured payload (NULL when the native event carried none).
-- agent_run_id has NO cascade: the log outlives its run row (permanent audit), exactly like the
-- workflow ledger.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent.

CREATE TABLE IF NOT EXISTS agent_run_event (
    id              UUID         NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_run_id    UUID         NOT NULL REFERENCES agent_run(id),
    sequence        BIGSERIAL    NOT NULL,
    kind            TEXT         NOT NULL,
    text            TEXT         NOT NULL,
    data_json       JSONB        NULL,
    occurred_at     TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Run-scoped chronological scan: every live-log read + replay traverses this with a sequence cursor.
CREATE INDEX IF NOT EXISTS idx_are_run_sequence ON agent_run_event(agent_run_id, sequence);

-- Append-only immutability: a live log must read at T+1 exactly what the agent emitted at T. A
-- DB-layer trigger makes that uncircumventable from the app (matches workflow_run_record). The
-- function is CREATE OR REPLACE and the trigger is DROP-then-CREATE so the script stays re-runnable.
CREATE OR REPLACE FUNCTION agent_run_event_reject_mutations() RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION
        'agent_run_event is append-only — UPDATE/DELETE rejected (run=%, sequence=%, kind=%). Insert a new event instead.',
        OLD.agent_run_id, OLD.sequence, OLD.kind;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS agent_run_event_enforce_immutability ON agent_run_event;
CREATE TRIGGER agent_run_event_enforce_immutability
    BEFORE UPDATE OR DELETE ON agent_run_event
    FOR EACH ROW EXECUTE FUNCTION agent_run_event_reject_mutations();

COMMENT ON TABLE agent_run_event IS
    'B0.3 — append-only event log of an agent run (the durable backing for the live progress stream). '
    'One row per normalized AgentEvent (kind + text + optional data_json), ordered by a global BIGSERIAL '
    'sequence and scoped per run via idx_are_run_sequence. kind is the closed AgentEventKind vocabulary. '
    'Immutability enforced by trigger; agent_run_id has no cascade (the log is permanent audit).';
