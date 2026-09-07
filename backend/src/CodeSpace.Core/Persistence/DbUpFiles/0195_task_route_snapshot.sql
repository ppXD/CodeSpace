-- Preview decisions bind exact intent and identity; they are never consent or execution authority.
-- Run/result consumption is committed in the same transaction as run staging.
CREATE TABLE task_route_snapshot (
    id uuid PRIMARY KEY,
    team_id uuid NOT NULL REFERENCES team(id) ON DELETE CASCADE,
    actor_user_id uuid NOT NULL REFERENCES app_user(id),
    input_digest text NOT NULL,
    seed_digest text NOT NULL,
    policy_fingerprint text NOT NULL,
    route_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    consumed_run_id uuid NULL REFERENCES workflow_run(id) ON DELETE CASCADE,
    result_json jsonb NULL,
    CONSTRAINT ck_task_route_snapshot_consumption CHECK ((consumed_run_id IS NULL) = (result_json IS NULL))
);
CREATE UNIQUE INDEX ux_task_route_snapshot_consumed_run_id ON task_route_snapshot(consumed_run_id);
CREATE INDEX ix_task_route_snapshot_expires_at ON task_route_snapshot(expires_at);
