-- Arc D / D1 — the lesson ledger: distilled cross-run learning, appended by the nightly post-mortem distiller.
-- Invariants:
--   * a lesson always CITES the runs that taught it (source_run_ids, never empty) — no citation, no lesson;
--   * a run distilled once is never re-distilled (the distiller excludes already-cited runs), so re-running a
--     window is idempotent and never duplicates;
--   * invalidation is temporal and one-way (invalidated_at, Graphiti-style): readers see only current rows,
--     history is never rewritten.
CREATE TABLE IF NOT EXISTS lesson (
    id uuid PRIMARY KEY,
    team_id uuid NOT NULL REFERENCES team(id),
    mode text NOT NULL,
    repository_id uuid NULL,
    failure_class text NOT NULL,
    what_failed text NOT NULL,
    why text NOT NULL,
    how_to_apply text NOT NULL,
    source_run_ids uuid[] NOT NULL,
    distilled_by_model text NOT NULL,
    valid_from timestamptz NOT NULL,
    invalidated_at timestamptz NULL,
    created_date timestamptz NOT NULL,
    created_by uuid NOT NULL,
    last_modified_date timestamptz NOT NULL,
    last_modified_by uuid NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_lesson_team_mode_current ON lesson (team_id, mode) WHERE invalidated_at IS NULL;
