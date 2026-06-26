-- 0076_workflow_rerun_lease.sql
--
-- An ACTIVE-rerun lease: a per-branch claim held while a flow.map branch-rerun fork is in flight, so two
-- CONCURRENT reruns can't re-run the SAME (original_run_id, map_node_id, branch_index) at once (which would
-- double-fire a side-effecting branch body). One row per re-run branch.
--
-- The guarantee is the UNIQUE PARTIAL index over (original_run_id, map_node_id, branch_index) WHERE
-- status = 'in_progress': a second rerun whose branch set OVERLAPS an in-flight lease loses the INSERT on 23505
-- (→ RerunAlreadyInProgressException → 409, its fork rolled back with the command transaction). DISJOINT branch
-- sets never collide. Complements the OperationId idempotency index (uq_wrr_idempotency_key, 0014): that dedups a
-- SAME-token resubmit before any lease is taken; this blocks DISTINCT-token concurrent overlap.
--
-- Release is keyed on fork_run_id: the engine flips the lease inline when the fork completes, and the reconciler's
-- terminal-join sweep is the complete backstop (every lease whose fork reached a terminal state). Both run FKs are
-- ON DELETE CASCADE, so a hard-deleted run also frees its leases.
--
-- Idempotent: CREATE TABLE/INDEX IF NOT EXISTS (DbUp journals the script).

CREATE TABLE IF NOT EXISTS workflow_rerun_lease (
    id              uuid        PRIMARY KEY,
    original_run_id uuid        NOT NULL REFERENCES workflow_run(id) ON DELETE CASCADE,
    map_node_id     text        NOT NULL,
    branch_index    integer     NOT NULL,
    fork_run_id     uuid        NOT NULL REFERENCES workflow_run(id) ON DELETE CASCADE,
    team_id         uuid        NOT NULL,
    status          text        NOT NULL,
    created_at      timestamptz NOT NULL,
    released_at     timestamptz
);

-- Only ONE in-progress lease per (original run, map, branch) — the concurrent-overlap guard.
CREATE UNIQUE INDEX IF NOT EXISTS uq_wrl_active_branch
    ON workflow_rerun_lease (original_run_id, map_node_id, branch_index)
    WHERE status = 'in_progress';

-- The reconciler terminal-join sweep filters in-progress leases by fork_run_id; index it.
CREATE INDEX IF NOT EXISTS ix_wrl_fork_run
    ON workflow_rerun_lease (fork_run_id)
    WHERE status = 'in_progress';
