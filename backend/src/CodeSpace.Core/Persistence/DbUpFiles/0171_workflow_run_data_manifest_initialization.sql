-- 0171_workflow_run_data_manifest_initialization.sql
-- One idempotent, bounded initialization for the facets that have fail-closed producer accounting. It runs before
-- the engine emits lifecycle records, so a producer death before its first expectation advance cannot be mistaken for
-- an undeclared/empty plane. Replays use ON CONFLICT DO NOTHING and do not churn revisions.

CREATE OR REPLACE FUNCTION workflow_run_data_manifest_initialize(
    team UUID, run UUID, facets TEXT[], contract_version INTEGER) RETURNS BIGINT AS $$
DECLARE
    stamped_at TIMESTAMPTZ;
    inserted BIGINT;
    open_anywhere BOOLEAN;
BEGIN
    PERFORM workflow_run_data_completeness_lock(team, run);
    stamped_at := clock_timestamp();
    SELECT EXISTS (SELECT 1 FROM workflow_run_capture_gap
                   WHERE team_id = team AND workflow_run_id = run AND resolution = 'Open') INTO open_anywhere;

    INSERT INTO workflow_run_data_manifest (
        id, team_id, workflow_run_id, facet, expected_record_count, present_record_count,
        known_missing_count, verdict, masked_observed, revision, schema_version, created_at, last_modified_at)
    SELECT gen_random_uuid(), team, run, facet_name, 0, 0,
           workflow_run_capture_gap_open_count(team, run, facet_name::varchar),
           CASE WHEN open_anywhere THEN 'Partial' ELSE 'Exact' END, FALSE,
           1, contract_version, stamped_at, stamped_at
    FROM unnest(facets) AS facet_name
    ON CONFLICT (team_id, workflow_run_id, facet) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION workflow_run_data_manifest_initialize(UUID, UUID, TEXT[], INTEGER) IS
    'Idempotently declares zero for the registered producer facets under the same per-run lock as every later advance; replay never revises existing statements.';
