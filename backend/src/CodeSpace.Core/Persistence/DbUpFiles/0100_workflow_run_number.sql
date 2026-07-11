-- 0100_workflow_run_number.sql
-- Give every run a team-scoped sequential number so it can be addressed by a clean URL
-- (/teams/{team}/runs/{number}, e.g. /runs/1042) instead of a raw GUID. Additive + backfilled +
-- non-breaking.
--
-- Allocation is a BEFORE INSERT trigger that claims the next number from a per-team counter row
-- (team_run_counter) with an atomic INSERT … ON CONFLICT DO UPDATE … RETURNING — row-locked so
-- flow.map's PARALLEL run creates serialise and get DISTINCT numbers by construction (no MAX+1
-- read-committed race). Gap-tolerant: a rolled-back run leaves a hole, which is fine for a display
-- id. Doing it in the DB (like run_kind's generated column, migration 0067) means every insert —
-- app path AND test seed — gets a number with no caller having to remember, and there is one
-- allocation site instead of one per run-staging path.

-- 1) Per-team counter. last_run_number is the highest number handed out so far (0 = none yet).
CREATE TABLE IF NOT EXISTS team_run_counter (
    team_id           UUID   NOT NULL PRIMARY KEY REFERENCES team(id),
    last_run_number   BIGINT NOT NULL DEFAULT 0
);

-- 2) The run's team-scoped number. Nullable first so the backfill can populate it.
ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS run_number BIGINT;

-- 3) Backfill existing runs: dense 1..N per team by creation order (runs are never deleted, so the
--    dense numbering stays unique under the index in step 5).
WITH ranked AS (
    SELECT id, ROW_NUMBER() OVER (PARTITION BY team_id ORDER BY created_date, id) AS rn
    FROM workflow_run
    WHERE run_number IS NULL
)
UPDATE workflow_run r SET run_number = ranked.rn
FROM ranked WHERE r.id = ranked.id;

-- 4) Seed the counter to each team's current max so newly-allocated numbers continue past the
--    backfill instead of colliding with it.
INSERT INTO team_run_counter (team_id, last_run_number)
SELECT team_id, MAX(run_number) FROM workflow_run GROUP BY team_id
ON CONFLICT (team_id) DO UPDATE SET last_run_number = EXCLUDED.last_run_number;

-- 5) The trigger: claim the next per-team number on insert when the row doesn't already carry one.
--    The upsert-and-increment RETURNING row-locks team_run_counter, so concurrent inserts serialise
--    and get distinct numbers. Runs inside the caller's transaction — a rolled-back insert rolls the
--    increment back too (leaving a gap, which is fine).
CREATE OR REPLACE FUNCTION assign_workflow_run_number() RETURNS TRIGGER AS $$
BEGIN
    IF NEW.run_number IS NULL THEN
        INSERT INTO team_run_counter (team_id, last_run_number)
        VALUES (NEW.team_id, 1)
        ON CONFLICT (team_id) DO UPDATE SET last_run_number = team_run_counter.last_run_number + 1
        RETURNING last_run_number INTO NEW.run_number;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_workflow_run_number ON workflow_run;
CREATE TRIGGER trg_workflow_run_number
    BEFORE INSERT ON workflow_run
    FOR EACH ROW EXECUTE FUNCTION assign_workflow_run_number();

-- 6) Every run now has a number (backfill for old rows, trigger for new) — enforce NOT NULL +
--    per-team uniqueness (the trigger's backstop).
ALTER TABLE workflow_run ALTER COLUMN run_number SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_run_team_number
    ON workflow_run (team_id, run_number);
