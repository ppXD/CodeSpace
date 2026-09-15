-- P15-5b-ii: the standing team cost cap every reservation is admitted against. Until now the ledger's only sum was
-- PER RUN, so a team could launch any number of runs each under its own modest cap and spend with no ceiling at all.
CREATE TABLE budget_team_cap (
    team_id uuid PRIMARY KEY REFERENCES team(id),
    cap_usd numeric NOT NULL CHECK (cap_usd > 0),
    cap_window text NOT NULL CHECK (btrim(cap_window) <> ''),
    created_date timestamptz NOT NULL,
    created_by uuid NOT NULL,
    last_modified_date timestamptz NOT NULL,
    last_modified_by uuid NOT NULL
);

COMMENT ON TABLE budget_team_cap IS 'At most one standing cost cap per team; absence falls back to the deployment cap, and absence of that keeps a per-run ceiling only.';
COMMENT ON COLUMN budget_team_cap.cap_window IS 'The rolling window the committed sum covers. Named cap_window because window is a reserved word.';

-- The team committed-sum query behind every admission: team + a created_date range scan, bounded to the cap's
-- window. state is filtered by NOT-equal (Released/Expired excluded), so it cannot serve as an equality middle
-- column here — a leading (team_id, created_date) index lets the range scan do the work instead.
--
-- LOCKS, stated honestly: DbUp runs the entire upgrade inside ONE transaction (DbUpRunner.BuildEngine calls
-- .WithTransaction()), and Postgres releases no lock before that transaction commits. budget_reservation is a
-- PRE-EXISTING table (0104) written on every model-call reservation, so this CREATE INDEX takes a SHARE lock that
-- blocks every INSERT into it for the length of the whole upgrade run, and the build itself scans the table.
-- CREATE INDEX CONCURRENTLY cannot run inside a transaction, so avoiding that block is a change to DbUpRunner, not
-- to this file — the same ceiling 0158 hit. IF NOT EXISTS keeps this idempotent, which lets an operator upgrading a
-- large deployment dodge the lock entirely by running, against that database, BEFORE this migration reaches it:
--     CREATE INDEX CONCURRENTLY ix_budget_reservation_team_created ON budget_reservation (team_id, created_date);
-- — this statement then finds it already built and no-ops.
CREATE INDEX IF NOT EXISTS ix_budget_reservation_team_created ON budget_reservation (team_id, created_date);
