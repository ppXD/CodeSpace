-- 0190_run_scorecard.sql
--
-- A4: ONE row per terminal contract-era workflow run carrying the north-star bits that until now were only ever
-- computed live over the most recent 100 runs and then discarded (UnattendedDeliveryScorecardService.RecentRunCap).
-- With nothing persisted, every CI real-model run was a point estimate that died with the process -- nobody could
-- say "the unattended-solve-with-delivery rate went from X to Y", and the lesson A/B arm (recorded per supervisor
-- decision since Arc D) was never rolled up, so injection's effect on the rate had never been measured at all.
--
-- OBSERVATION-ONLY. Nothing in the engine reads this table, and the three live-computed scorecard endpoints keep
-- computing their numbers exactly as before -- a missing or stale row can never change a verdict, only a trend.
--
-- UPSERT-by-run (unique on workflow_run_id), NOT append-only: this is a projection of one run's SETTLED facts, not
-- a history of what each sweep pass thought. The assessment history already lives in completion_assessment; a
-- second append-only tape of the same facts would only invite the two to disagree.
--
-- effort_mode is deliberately declared but UNPOPULATED: the router's effort tier (TaskEffortModes) is not
-- denormalised onto workflow_run the way projection_kind is (WorkflowRun.ProjectionKind), and no per-run durable
-- fact carries it today. The column is here so the shape does not change when a launch seam starts recording it;
-- until then every row reads NULL, which is honest rather than inferred.
--
-- Rollback: DROP TABLE run_scorecard;
-- Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS run_scorecard (
    id                              UUID          NOT NULL PRIMARY KEY,
    team_id                         UUID          NOT NULL REFERENCES team(id),
    workflow_run_id                 UUID          NOT NULL,
    completed_at                    TIMESTAMPTZ   NOT NULL,
    projection_kind                 VARCHAR(60)   NULL,
    effort_mode                     VARCHAR(20)   NULL,
    solved                          BOOLEAN       NOT NULL,
    delivered                       BOOLEAN       NOT NULL,
    human_touches                   INTEGER       NOT NULL,
    unattended_solved_with_delivery BOOLEAN       NOT NULL,
    cost_usd                        NUMERIC(18,6) NULL,
    brain_plane_usd                 NUMERIC(18,6) NULL,
    lesson_arm                      VARCHAR(16)   NULL,
    brain_model                     VARCHAR(200)  NULL,
    scorer_version                  VARCHAR(60)   NOT NULL,
    created_date                    TIMESTAMPTZ   NOT NULL,
    created_by                      UUID          NOT NULL,
    last_modified_date              TIMESTAMPTZ   NOT NULL,
    last_modified_by                UUID          NOT NULL,
    CONSTRAINT ux_run_scorecard_run UNIQUE (workflow_run_id),
    CONSTRAINT ck_run_scorecard_touches CHECK (human_touches >= 0),
    -- The scorer's headline definition, pinned in the schema: solved AND delivered AND zero human touches. A future
    -- edit that quietly widens the numerator has to say so here (and bump scorer_version) instead of drifting.
    CONSTRAINT ck_run_scorecard_headline CHECK (unattended_solved_with_delivery = (solved AND delivered AND human_touches = 0)),
    CONSTRAINT ck_run_scorecard_lesson_arm CHECK (lesson_arm IS NULL OR lesson_arm IN ('injected', 'withheld', 'none'))
);

-- The trend query's access path: one team's window, newest first.
CREATE INDEX IF NOT EXISTS ix_run_scorecard_team_completed ON run_scorecard (team_id, completed_at DESC);
-- The by-arm slice's access path.
CREATE INDEX IF NOT EXISTS ix_run_scorecard_team_arm ON run_scorecard (team_id, lesson_arm);

COMMENT ON TABLE run_scorecard IS
    'One durable north-star row per terminal contract-era run: solved/delivered/human-touches, its headline bit, spend, and the lesson A/B arm. Observation-only; upserted by run.';
