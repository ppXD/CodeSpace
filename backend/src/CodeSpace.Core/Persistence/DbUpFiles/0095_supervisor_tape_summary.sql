-- 0095_supervisor_tape_summary.sql
--
-- P1.2 of the launch-stability arc: the supervisor's decision-tape AUTO-COMPACT. A long run's prior-decision prompt
-- grows unboundedly; when it overflows the brain model's context window the decider folds the OLDEST decisions into a
-- model-written digest and persists it HERE — one rolling row per run (re-compacted forward as the run keeps growing),
-- loaded at rehydrate so every later turn renders [digest + recent tail] instead of the whole tape. PROMPT-GRAIN ONLY:
-- the decision ledger stays the complete tape (bounds / recitation / replay are untouched), so this table is derived
-- state — losing a row merely costs a re-compaction. `supervisor_run_id` is UNIQUE (the rolling row) and a soft link
-- (no FK) like the ledger's. Additive + idempotent.

CREATE TABLE IF NOT EXISTS supervisor_tape_summary (
    id                  UUID         NOT NULL PRIMARY KEY,
    team_id             UUID         NOT NULL REFERENCES team(id),
    supervisor_run_id   UUID         NOT NULL,                 -- soft link (no FK); UNIQUE = one rolling digest per run
    up_to_sequence      BIGINT       NOT NULL,                 -- the highest ledger sequence folded into the digest
    summary             TEXT         NOT NULL,                 -- the model-written progress digest
    created_date        TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_date        TIMESTAMPTZ  NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_supervisor_tape_summary_run ON supervisor_tape_summary (supervisor_run_id);
