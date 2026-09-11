-- 0229_agent_run_cleanup_receipt.sql
--
-- A run abandoned on a DEAD host left nothing behind that named what it left behind.
--
-- The reconciler's abandon path runs on whichever worker Hangfire hands the sweep to, and when a handle's LaunchHost
-- is some other host it abandons WITHOUT being able to reach a single one of that run's resources: the spool is a
-- directory on the dead host, the netns / cgroup are kernel objects in the dead host's namespaces, the MCP socket is
-- an inode inside that spool, the workspace is a clone on that disk. It called the local netns and cgroup teardowns
-- anyway — against keys that name nothing here — and swallowed the miss as LogWarning("best-effort ... failed"). The
-- egress subnet is the sharpest edge: FilteredEgressNetns.TeardownAsync releases the LOCAL .egress-subnets lease, so
-- the DEAD host's lease was never released, and the allocator's space is bounded.
--
-- So the run reached Failed with no record anywhere of the resources still standing, and nothing addressed to the
-- host that could still reclaim them. An operator could not ask "what did this run leave, and where?" and a sweep on
-- the owning host had nothing to find.
--
-- This table is that record: ONE ROW PER RESOURCE, not one boolean per run, because these resources live in
-- different places and are reclaimed by different tools. An Orphaned row is a claim that something REAL is still
-- sitting on a NAMED host, which is why owner_host is mandatory for exactly that outcome (ck_..._orphan): an orphan
-- with no owner host cannot be found by any sweep.
--
-- Identity is the RESOURCE, not the statement — (agent_run_id, kind, resource_key) — so each resource carries one
-- current, readable answer instead of an append-only log a reader must fold. resource_key is nullable and folded
-- through COALESCE in the unique index because some kinds have no key (a credential lease names nothing on disk),
-- and NULL would otherwise defeat the constraint entirely.
--
-- The outcome vocabulary keeps two terminal-good values apart on purpose. Completed means cleanup ran on the owning
-- host when the run ended. Compensated means the resource WAS orphaned and a later sweep on its own host reclaimed
-- it — the run did leave something behind for a while, and collapsing that into Completed would erase the only
-- evidence that the cross-host gap is real. Unknown is reserved for what genuinely cannot be established: a teardown
-- this host cannot attempt at all, and the injected model credential, which may have been mid-use when the host
-- died and which no sweep can ever prove otherwise about.
--
-- Rollback: DROP TABLE agent_run_cleanup_receipt.

CREATE TABLE agent_run_cleanup_receipt (
    id uuid PRIMARY KEY,
    team_id uuid NOT NULL,
    agent_run_id uuid NOT NULL,
    fence_epoch bigint NOT NULL,
    kind text NOT NULL,
    outcome text NOT NULL,
    owner_host text,
    resource_key text,
    recorded_by_host text NOT NULL,
    recorded_at timestamptz NOT NULL,
    error_code text,
    CONSTRAINT fk_agent_run_cleanup_receipt_run FOREIGN KEY (team_id, agent_run_id)
        REFERENCES agent_run (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_agent_run_cleanup_receipt_kind CHECK (
        kind IN ('Spool', 'LogSegments', 'McpSocket', 'EgressSubnet', 'Cgroup', 'Workspace', 'ProviderCredentialLease')),
    CONSTRAINT ck_agent_run_cleanup_receipt_outcome CHECK (
        outcome IN ('Completed', 'Compensated', 'Orphaned', 'Unknown')),
    CONSTRAINT ck_agent_run_cleanup_receipt_orphan CHECK (
        outcome <> 'Orphaned' OR (owner_host IS NOT NULL AND btrim(owner_host) <> '')),
    CONSTRAINT ck_agent_run_cleanup_receipt_settled CHECK (
        outcome NOT IN ('Completed', 'Compensated') OR error_code IS NULL),
    CONSTRAINT ck_agent_run_cleanup_receipt_shape CHECK (
        id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND fence_epoch >= 0
        AND btrim(recorded_by_host) <> ''
        AND (owner_host IS NULL OR btrim(owner_host) <> '')
        AND (resource_key IS NULL OR btrim(resource_key) <> '')
        AND (error_code IS NULL OR btrim(error_code) <> ''))
);

-- One current answer per resource. COALESCE folds the keyless kinds so they cannot accumulate duplicates.
CREATE UNIQUE INDEX ux_agent_run_cleanup_receipt_resource
    ON agent_run_cleanup_receipt (agent_run_id, kind, COALESCE(resource_key, ''));

-- The owning host's sweep reads exactly this: its own outstanding orphans, oldest first. Partial, because a settled
-- row is never swept again, and lower() because that is how the runner compares host identities everywhere else.
CREATE INDEX ix_agent_run_cleanup_receipt_orphan_host
    ON agent_run_cleanup_receipt (lower(owner_host), recorded_at, id)
    WHERE outcome = 'Orphaned';

-- The Room's per-turn read: every receipt of the turn's agent runs, in one narrow team-scoped query.
CREATE INDEX ix_agent_run_cleanup_receipt_run
    ON agent_run_cleanup_receipt (team_id, agent_run_id, kind);

COMMENT ON TABLE agent_run_cleanup_receipt IS
    'One typed statement per agent-run resource: what it was, which host owns it, and what became of it. An Orphaned row is addressed to owner_host, whose own sweep compensates it.';
