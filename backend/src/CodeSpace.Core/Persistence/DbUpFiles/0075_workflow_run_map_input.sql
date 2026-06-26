-- 0075_workflow_run_map_input.sql
--
-- A flow.map's RESOLVED collection, frozen ONCE at the map's first fan-out and read by every later re-entry
-- (same-run suspend/resume) INSTEAD of re-resolving the `items` binding from live scope. Without this, a branch
-- that suspends and resumes after an upstream output changed would re-resolve a DIFFERENT array (length/order/
-- content) — silently shifting branch indices and corrupting the index-keyed branch replay + ordered reduce.
--
-- Keyed by (run_id, map_node_id, iteration_key) where iteration_key is the map's OWN enclosing-container key
-- (empty string at top level, the loop/iterate key when nested) — NOT the per-branch "<mapId>#<i>" key — so a map
-- nested in a loop iteration gets ONE row per outer pass. The UNIQUE index makes the engine's get-or-create
-- race-safe: a concurrent re-walk / crash-replay loses the INSERT on 23505 and re-reads the winning row.
--
-- Secret rule (mirrors workflow_run_variable): a SecretDerived collection (its `items` binding references a
-- secret path) stores elements_json = NULL and is re-resolved live on read, so no plaintext secret is frozen at
-- rest and the map's resume behaviour is unchanged from a pre-snapshot run.
--
-- Idempotent: CREATE TABLE/INDEX IF NOT EXISTS + a re-addable CHECK constraint (DbUp journals the script).

CREATE TABLE IF NOT EXISTS workflow_run_map_input (
    id              uuid        PRIMARY KEY,
    run_id          uuid        NOT NULL REFERENCES workflow_run(id) ON DELETE CASCADE,
    map_node_id     text        NOT NULL,
    iteration_key   text        NOT NULL,
    definition_hash text        NOT NULL,
    element_count   integer     NOT NULL,
    elements_json   text,
    content_hash    text        NOT NULL,
    sensitivity     text        NOT NULL,
    captured_at     timestamptz NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_wrmi_run_map_iteration
    ON workflow_run_map_input (run_id, map_node_id, iteration_key);

-- A SecretDerived snapshot never stores a frozen array (re-resolved live); a Plain snapshot always does.
ALTER TABLE workflow_run_map_input DROP CONSTRAINT IF EXISTS ck_wrmi_secret_elements_null;
ALTER TABLE workflow_run_map_input ADD CONSTRAINT ck_wrmi_secret_elements_null
    CHECK (sensitivity <> 'SecretDerived' OR elements_json IS NULL);
