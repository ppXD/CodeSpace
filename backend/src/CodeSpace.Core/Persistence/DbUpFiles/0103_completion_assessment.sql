-- 0103_completion_assessment.sql
--
-- P2a-4 (v4.2-FINAL, Shadow phase): the durable, APPEND-ONLY assessment record. One row per compose of a terminal
-- contract-era run -- the Shadow sweep appends when the latest row's assessment differs (or none exists), so the
-- history of "what the protocol would have said" survives watermark-driven re-composes (Lock Clause 2's append
-- law, ahead of P2b binding the terminal CAS to it). legacy_is_solved snapshots the LEGACY scorecard ladder's
-- verdict AT COMPOSE TIME -- the degraded-inflation delta (assessment says Unsolved, legacy says Solved) becomes a
-- standing SQL query instead of a one-off audit. Shadow NEVER mutates workflow_run (Lock Clause 1).
-- Rollback: DROP TABLE completion_assessment;
-- Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS completion_assessment (
    id                    UUID        NOT NULL PRIMARY KEY,
    team_id               UUID        NOT NULL REFERENCES team(id),
    workflow_run_id       UUID        NOT NULL,
    enforcement_mode      TEXT        NOT NULL,
    basis                 TEXT        NOT NULL,
    outcome               TEXT        NOT NULL,
    verification          TEXT        NOT NULL,
    assessment_jsonb      JSONB       NOT NULL,
    legacy_is_solved      BOOLEAN     NOT NULL,
    rejection_count       INTEGER     NOT NULL DEFAULT 0,
    contract_error_count  INTEGER     NOT NULL DEFAULT 0,
    created_date          TIMESTAMPTZ NOT NULL,
    created_by            UUID        NOT NULL,
    last_modified_date    TIMESTAMPTZ NOT NULL,
    last_modified_by      UUID        NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_completion_assessment_run ON completion_assessment (workflow_run_id, created_date);
CREATE INDEX IF NOT EXISTS ix_completion_assessment_team ON completion_assessment (team_id);
