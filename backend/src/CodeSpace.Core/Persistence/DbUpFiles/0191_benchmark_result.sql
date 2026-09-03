-- 0191_benchmark_result.sql
--
-- A4: one durable row per benchmark (task x mode) cell the corpus runner actually ran. Until now a corpus run's
-- per-cell results reached the GitHub step summary and nowhere else -- the solve rate was re-derived from scratch
-- every CI run and never comparable across runs, commits, or model bundles.
--
-- ADDITIVE and OBSERVATION-ONLY: the benchmark gate's verdict is still computed from the in-memory
-- CorpusBenchmarkRun (scorecard + fixed-denominator cells). A failed write is swallowed by the runner, so a
-- persistence fault can neither fail nor pass a gate that would otherwise have gone the other way.
--
-- suite_version is the corpus's IDENTITY here (EvalSuiteManifest.Version -- content-derived, algorithm-prefixed).
-- There is no free-standing "corpus name" in the contract to record: a corpus is a list of BenchmarkTask, and its
-- only stable identity is that hash. Recording an invented label would be a name nothing else could join on.
--
-- Rollback: DROP TABLE benchmark_result;
-- Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS benchmark_result (
    id                UUID             NOT NULL PRIMARY KEY,
    team_id           UUID             NOT NULL REFERENCES team(id),
    suite_version     VARCHAR(120)     NOT NULL,
    task_id           VARCHAR(200)     NOT NULL,
    mode              VARCHAR(40)      NOT NULL,
    harness           VARCHAR(60)      NULL,
    model             VARCHAR(200)     NULL,
    agent_run_id      UUID             NULL,
    solved            BOOLEAN          NOT NULL,
    run_status        VARCHAR(20)      NOT NULL,
    -- The intervention flags: how many bounded revise rounds the executor spent, whether the run got the FULL MCP
    -- tool catalog, and the terminal exit reason (the critic-flag path is "output-flagged"). A solve rate that rode
    -- on extra attempts is visible here rather than hidden in the headline.
    revise_rounds     INTEGER          NOT NULL,
    mcp_full_catalog  BOOLEAN          NOT NULL,
    exit_reason       VARCHAR(60)      NULL,
    cost_usd          NUMERIC(18,6)    NULL,
    duration_seconds  DOUBLE PRECISION NULL,
    git_sha           VARCHAR(60)      NULL,
    ci_run_id         VARCHAR(40)      NULL,
    created_date      TIMESTAMPTZ      NOT NULL,
    created_by        UUID             NOT NULL,
    last_modified_date TIMESTAMPTZ     NOT NULL,
    last_modified_by  UUID             NOT NULL,
    CONSTRAINT ck_benchmark_result_revise_rounds CHECK (revise_rounds >= 0)
);

-- Append-only by construction (a re-run of the same cell is a NEW measurement, not a correction), so the access
-- path is "this suite's cells, newest first" rather than a uniqueness constraint.
CREATE INDEX IF NOT EXISTS ix_benchmark_result_suite ON benchmark_result (team_id, suite_version, created_date DESC);
CREATE INDEX IF NOT EXISTS ix_benchmark_result_task ON benchmark_result (team_id, task_id, mode);

COMMENT ON TABLE benchmark_result IS
    'One row per benchmark (task x mode) cell the corpus runner ran: the objective grade, intervention flags, spend, and CI provenance. Append-only, observation-only.';
