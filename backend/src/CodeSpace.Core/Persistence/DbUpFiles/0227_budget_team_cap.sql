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

-- The team committed-sum query behind every admission: team + live/settled state, bounded to the cap's window.
CREATE INDEX ix_budget_reservation_team_state_created ON budget_reservation (team_id, state, created_date);
