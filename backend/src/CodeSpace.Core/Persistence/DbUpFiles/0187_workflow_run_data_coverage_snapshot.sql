-- 0187_workflow_run_data_coverage_snapshot.sql
--
-- A manifest states HOW COMPLETE one producer's record is. It did not durably state WHICH producers applied to one
-- run: the reader used the process-wide four-facet list. Extending that list would retroactively change historical
-- runs, while leaving it fixed made conditional producers invisible. These two new tables persist applicability.
--
-- This is intentionally an ONLINE, takeover-on-write migration. It does not scan or ALTER either existing data table,
-- and it adds no full-table foreign key. DbUp holds locks until the whole upgrade transaction commits, so even an
-- apparently online NOT VALID replacement at the start of this script would block manifest writers throughout every
-- later scan. Historical runs remain on the frozen LegacyV1 read shape until their first post-upgrade writer. That
-- writer adopts every existing statement under the per-run rendezvous before doing anything else. New runs persist
-- their caller's baseline immediately. The two shapes therefore meet without a cutover population or status race.

CREATE TABLE workflow_run_data_coverage (
    id UUID PRIMARY KEY,
    team_id UUID NOT NULL,
    workflow_run_id UUID NOT NULL,
    state VARCHAR(12) NOT NULL,
    generation INTEGER NOT NULL DEFAULT 1,
    revision BIGINT NOT NULL DEFAULT 1,
    baseline_facets VARCHAR(48)[] NOT NULL,
    schema_version INTEGER NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    last_modified_at TIMESTAMPTZ NOT NULL,
    sealed_at TIMESTAMPTZ NULL,
    CONSTRAINT fk_workflow_run_data_coverage_team FOREIGN KEY (team_id) REFERENCES team(id) ON DELETE RESTRICT,
    CONSTRAINT fk_workflow_run_data_coverage_run FOREIGN KEY (team_id, workflow_run_id) REFERENCES workflow_run(team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ux_workflow_run_data_coverage_run UNIQUE (team_id, workflow_run_id),
    CONSTRAINT ck_workflow_run_data_coverage_state CHECK
        (state IN ('Open', 'Sealed') AND ((state = 'Open' AND sealed_at IS NULL) OR (state = 'Sealed' AND sealed_at IS NOT NULL))),
    CONSTRAINT ck_workflow_run_data_coverage_bounds CHECK (generation > 0 AND revision > 0 AND cardinality(baseline_facets) > 0 AND cardinality(baseline_facets) <= 100 AND array_position(baseline_facets, NULL) IS NULL AND schema_version > 0),
    CONSTRAINT ck_workflow_run_data_coverage_time CHECK
        (last_modified_at >= created_at AND (sealed_at IS NULL OR sealed_at >= created_at))
);

CREATE TABLE workflow_run_data_coverage_facet (
    id UUID PRIMARY KEY,
    team_id UUID NOT NULL,
    workflow_run_id UUID NOT NULL,
    facet VARCHAR(48) NOT NULL,
    ordinal INTEGER NOT NULL,
    declared_generation INTEGER NOT NULL,
    schema_version INTEGER NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT fk_workflow_run_data_coverage_facet_header FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run_data_coverage(team_id, workflow_run_id) ON DELETE RESTRICT,
    CONSTRAINT ux_workflow_run_data_coverage_facet UNIQUE (team_id, workflow_run_id, facet),
    CONSTRAINT ux_workflow_run_data_coverage_ordinal UNIQUE (team_id, workflow_run_id, ordinal),
    CONSTRAINT ck_workflow_run_data_coverage_facet_bounds CHECK
        (ordinal > 0 AND ordinal <= 100 AND declared_generation > 0 AND schema_version > 0),
    CONSTRAINT ck_workflow_run_data_coverage_facet_name CHECK (facet IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'node-output', 'deliverable', 'capture-gap', 'data-manifest'))
);

COMMENT ON TABLE workflow_run_data_coverage IS
    'Run-owned applicability boundary for completeness. Open admits conditional facets; Sealed freezes membership; continue reopens a new generation. Observation-only.';
COMMENT ON TABLE workflow_run_data_coverage_facet IS
    'Append-only facets applicable to one run. Ordinal preserves its frozen baseline and deterministic conditional suffix.';

CREATE OR REPLACE FUNCTION workflow_run_data_coverage_ensure(
    team UUID, run UUID, baseline_facets TEXT[], contract_version INTEGER) RETURNS BOOLEAN AS $$
DECLARE
    run_status TEXT;
    stamped_at TIMESTAMPTZ;
    inserted BIGINT;
    generation_now INTEGER;
    baseline_count INTEGER;
    effective_baseline TEXT[];
    existing_facets TEXT[];
BEGIN
    PERFORM workflow_run_data_completeness_lock(team, run);

    SELECT COUNT(DISTINCT facet_name)::integer INTO baseline_count FROM unnest(baseline_facets) AS facet_name;
    IF baseline_count = 0 OR baseline_count > 100 OR baseline_count <> cardinality(baseline_facets) THEN
        RAISE EXCEPTION 'coverage baseline must contain between 1 and 100 distinct non-null facets'
            USING ERRCODE = 'program_limit_exceeded';
    END IF;

    SELECT status INTO run_status FROM workflow_run WHERE team_id = team AND id = run;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow run % / % does not exist', team, run USING ERRCODE = 'foreign_key_violation';
    END IF;

    SELECT COALESCE(array_agg(facet ORDER BY facet), ARRAY[]::varchar[]) INTO existing_facets
    FROM workflow_run_data_manifest WHERE team_id = team AND workflow_run_id = run;
    effective_baseline := CASE WHEN cardinality(existing_facets) > 0
        THEN ARRAY['model-call', 'harness-execution', 'harness-process-attempt', 'native-record']::TEXT[]
        ELSE baseline_facets END;
    IF cardinality(effective_baseline) + (SELECT COUNT(*) FROM unnest(existing_facets) AS existing(facet)
        WHERE NOT (existing.facet = ANY(effective_baseline))) > 100 THEN
        RAISE EXCEPTION 'legacy coverage takeover exceeds the bounded 100-facet contract'
            USING ERRCODE = 'program_limit_exceeded';
    END IF;

    stamped_at := clock_timestamp();
    INSERT INTO workflow_run_data_coverage (
        id, team_id, workflow_run_id, state, generation, revision, baseline_facets, schema_version, created_at, last_modified_at, sealed_at)
    VALUES (gen_random_uuid(), team, run,
            CASE WHEN run_status IN ('Success', 'Failure', 'Cancelled') THEN 'Sealed' ELSE 'Open' END,
            1, 1, effective_baseline, contract_version, stamped_at, stamped_at,
            CASE WHEN run_status IN ('Success', 'Failure', 'Cancelled') THEN stamped_at ELSE NULL END)
    ON CONFLICT (team_id, workflow_run_id) DO NOTHING;
    GET DIAGNOSTICS inserted = ROW_COUNT;

    IF inserted > 0 THEN
        SELECT generation INTO generation_now FROM workflow_run_data_coverage
        WHERE team_id = team AND workflow_run_id = run;

        INSERT INTO workflow_run_data_coverage_facet (
            id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at)
        SELECT gen_random_uuid(), team, run, listed.facet_name, listed.ordinal, generation_now, contract_version, stamped_at
        FROM unnest(effective_baseline) WITH ORDINALITY AS listed(facet_name, ordinal)
        ORDER BY listed.ordinal;

        INSERT INTO workflow_run_data_coverage_facet (
            id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at)
        SELECT gen_random_uuid(), team, run, extra.facet,
               cardinality(effective_baseline) + ROW_NUMBER() OVER (ORDER BY extra.facet),
               generation_now, contract_version, stamped_at
        FROM unnest(existing_facets) AS extra(facet)
        WHERE NOT (extra.facet = ANY(effective_baseline))
        ORDER BY extra.facet;
    END IF;

    RETURN inserted > 0;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_data_coverage_declare_facet(
    team UUID, run UUID, facet_name TEXT, expected_delta BIGINT, baseline_facets TEXT[], contract_version INTEGER)
    RETURNS void AS $$
DECLARE
    coverage_state TEXT;
    generation_now INTEGER;
    next_ordinal INTEGER;
BEGIN
    PERFORM workflow_run_data_completeness_lock(team, run);
    IF EXISTS (SELECT 1 FROM workflow_run_data_coverage_facet
               WHERE team_id = team AND workflow_run_id = run AND facet = facet_name) THEN RETURN; END IF;

    IF NOT EXISTS (SELECT 1 FROM workflow_run_data_coverage WHERE team_id = team AND workflow_run_id = run) THEN
        PERFORM workflow_run_data_coverage_ensure(team, run, baseline_facets, contract_version);
        IF EXISTS (SELECT 1 FROM workflow_run_data_coverage_facet
                   WHERE team_id = team AND workflow_run_id = run AND facet = facet_name) THEN RETURN; END IF;
    END IF;

    SELECT state, generation INTO coverage_state, generation_now
    FROM workflow_run_data_coverage WHERE team_id = team AND workflow_run_id = run;
    IF coverage_state <> 'Open' THEN
        RAISE EXCEPTION 'workflow run % / % coverage is sealed; facet % is not in its terminal snapshot', team, run, facet_name
            USING ERRCODE = 'check_violation';
    END IF;
    IF expected_delta IS NULL OR expected_delta <= 0 THEN
        RAISE EXCEPTION 'new coverage facet % requires a positive producer declaration; present-only accounting cannot establish applicability', facet_name
            USING ERRCODE = 'check_violation';
    END IF;

    SELECT COALESCE(MAX(ordinal), 0) + 1 INTO next_ordinal
    FROM workflow_run_data_coverage_facet WHERE team_id = team AND workflow_run_id = run;
    IF next_ordinal > 100 THEN
        RAISE EXCEPTION 'workflow run % / % coverage exceeds the bounded 100-facet contract', team, run
            USING ERRCODE = 'program_limit_exceeded';
    END IF;
    INSERT INTO workflow_run_data_coverage_facet (
        id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at)
    VALUES (gen_random_uuid(), team, run, facet_name, next_ordinal, generation_now, contract_version, clock_timestamp());
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_data_manifest_admit_coverage() RETURNS trigger AS $$
BEGIN
    PERFORM workflow_run_data_coverage_declare_facet(
        NEW.team_id, NEW.workflow_run_id, NEW.facet, NEW.expected_record_count,
        ARRAY['model-call', 'harness-execution', 'harness-process-attempt', 'native-record']::TEXT[], NEW.schema_version);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_data_coverage_facet_immutable() RETURNS trigger AS $$
DECLARE
    coverage_state TEXT;
    coverage_generation INTEGER;
    baseline_snapshot TEXT[];
    baseline_position INTEGER;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'workflow_run_data_coverage_facet is append-only; run applicability cannot be revised or deleted';
    END IF;
    PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.workflow_run_id);

    SELECT coverage.state, coverage.generation, coverage.baseline_facets
    INTO coverage_state, coverage_generation, baseline_snapshot
    FROM workflow_run_data_coverage AS coverage
    WHERE coverage.team_id = NEW.team_id AND coverage.workflow_run_id = NEW.workflow_run_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow_run_data_coverage_facet requires its run coverage header' USING ERRCODE = 'foreign_key_violation';
    END IF;
    IF NEW.declared_generation <> coverage_generation THEN
        RAISE EXCEPTION 'workflow_run_data_coverage_facet must name the open header generation';
    END IF;
    baseline_position := array_position(baseline_snapshot, NEW.facet);
    IF (baseline_position IS NOT NULL AND NEW.ordinal <> baseline_position)
       OR (baseline_position IS NULL AND NEW.ordinal <= cardinality(baseline_snapshot)) THEN
        RAISE EXCEPTION 'workflow_run_data_coverage_facet must match the frozen baseline facet and ordinal';
    END IF;
    IF coverage_state = 'Sealed' AND baseline_position IS NULL AND NOT EXISTS (
        SELECT 1 FROM workflow_run_data_manifest
        WHERE team_id = NEW.team_id AND workflow_run_id = NEW.workflow_run_id AND facet = NEW.facet) THEN
        RAISE EXCEPTION 'workflow_run_data_coverage_facet cannot append conditional applicability to a sealed run';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_data_coverage_follow_status() RETURNS trigger AS $$
DECLARE
    was_terminal BOOLEAN;
    is_terminal BOOLEAN;
    stamped_at TIMESTAMPTZ;
BEGIN
    was_terminal := OLD.status IN ('Success', 'Failure', 'Cancelled');
    is_terminal := NEW.status IN ('Success', 'Failure', 'Cancelled');
    IF was_terminal = is_terminal THEN RETURN NEW; END IF;
    PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.id);
    stamped_at := clock_timestamp();
    IF is_terminal THEN
        UPDATE workflow_run_data_coverage SET state = 'Sealed', sealed_at = stamped_at,
            revision = revision + 1, last_modified_at = GREATEST(last_modified_at, stamped_at)
        WHERE team_id = NEW.team_id AND workflow_run_id = NEW.id AND state = 'Open';
    ELSE
        UPDATE workflow_run_data_coverage SET state = 'Open', sealed_at = NULL, generation = generation + 1,
            revision = revision + 1, last_modified_at = GREATEST(last_modified_at, stamped_at)
        WHERE team_id = NEW.team_id AND workflow_run_id = NEW.id AND state = 'Sealed';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_data_manifest_initialize(
    team UUID, run UUID, facets TEXT[], contract_version INTEGER) RETURNS BIGINT AS $$
DECLARE
    stamped_at TIMESTAMPTZ;
    inserted BIGINT;
BEGIN
    PERFORM workflow_run_data_completeness_lock(team, run);
    PERFORM workflow_run_data_coverage_ensure(team, run, facets, contract_version);
    stamped_at := clock_timestamp();
    INSERT INTO workflow_run_data_manifest (
        id, team_id, workflow_run_id, facet, expected_record_count, present_record_count,
        known_missing_count, verdict, masked_observed, expectation_declared, revision, schema_version, created_at, last_modified_at)
    SELECT gen_random_uuid(), team, run, listed.facet_name, NULL::BIGINT, 0, gaps.open_here,
           CASE WHEN gaps.open_here > 0 THEN 'Partial' ELSE 'LegacyUnknown' END, FALSE, FALSE,
           1, contract_version, stamped_at, stamped_at
    FROM workflow_run_data_coverage AS coverage
    CROSS JOIN LATERAL unnest(coverage.baseline_facets) WITH ORDINALITY AS listed(facet_name, ordinal)
    JOIN workflow_run_data_coverage_facet AS member
      ON member.team_id = coverage.team_id AND member.workflow_run_id = coverage.workflow_run_id
     AND member.facet = listed.facet_name AND member.ordinal = listed.ordinal
    CROSS JOIN LATERAL (SELECT workflow_run_capture_gap_open_count(team, run, listed.facet_name::varchar) AS open_here) AS gaps
    WHERE coverage.team_id = team AND coverage.workflow_run_id = run
    ON CONFLICT (team_id, workflow_run_id, facet) DO NOTHING;
    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_data_manifest_advance_covered(
    team UUID, run UUID, facet_name TEXT, expected_delta BIGINT, present_delta BIGINT, masked BOOLEAN,
    baseline_facets TEXT[], contract_version INTEGER) RETURNS void AS $$
BEGIN
    IF expected_delta = 0 AND NOT EXISTS (
        SELECT 1 FROM workflow_run_data_manifest
        WHERE team_id = team AND workflow_run_id = run AND facet = facet_name) THEN
        RAISE EXCEPTION 'present-only accounting cannot create the first statement for coverage facet %', facet_name
            USING ERRCODE = 'check_violation';
    END IF;
    PERFORM workflow_run_data_coverage_declare_facet(team, run, facet_name, expected_delta, baseline_facets, contract_version);
    PERFORM workflow_run_data_manifest_advance(team, run, facet_name, expected_delta, present_delta, masked, contract_version);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_data_manifest_advance_covered_batch(
    team UUID, run UUID, facet_names TEXT[], expected_deltas BIGINT[], present_deltas BIGINT[], masked_values BOOLEAN[],
    baseline_facets TEXT[], contract_version INTEGER) RETURNS void AS $$
DECLARE
    item INTEGER;
    item_count INTEGER;
BEGIN
    PERFORM workflow_run_data_completeness_lock(team, run);
    item_count := COALESCE(array_length(facet_names, 1), 0);
    IF item_count <> COALESCE(array_length(expected_deltas, 1), 0)
       OR item_count <> COALESCE(array_length(present_deltas, 1), 0)
       OR item_count <> COALESCE(array_length(masked_values, 1), 0) THEN
        RAISE EXCEPTION 'coverage batch arrays must have the same length' USING ERRCODE = 'array_subscript_error';
    END IF;
    IF item_count > 100 THEN
        RAISE EXCEPTION 'coverage batch exceeds the bounded 100-facet contract' USING ERRCODE = 'program_limit_exceeded';
    END IF;
    IF (SELECT COUNT(DISTINCT facet) FROM unnest(facet_names) AS facet) <> item_count THEN
        RAISE EXCEPTION 'coverage batch may advance each facet at most once' USING ERRCODE = 'unique_violation';
    END IF;
    FOR item IN 1..item_count LOOP
        IF expected_deltas[item] = 0 AND NOT EXISTS (
            SELECT 1 FROM workflow_run_data_manifest
            WHERE team_id = team AND workflow_run_id = run AND facet = facet_names[item]) THEN CONTINUE; END IF;
        PERFORM workflow_run_data_coverage_declare_facet(
            team, run, facet_names[item], expected_deltas[item], baseline_facets, contract_version);
        PERFORM workflow_run_data_manifest_advance(
            team, run, facet_names[item], expected_deltas[item], present_deltas[item], masked_values[item], contract_version);
    END LOOP;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_data_coverage_guard() RETURNS trigger AS $$
DECLARE
    run_status TEXT;
    run_is_terminal BOOLEAN;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow_run_data_coverage is durable run history and cannot be deleted';
    END IF;
    PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.workflow_run_id);
    SELECT status INTO run_status FROM workflow_run WHERE team_id = NEW.team_id AND id = NEW.workflow_run_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow run % / % does not exist', NEW.team_id, NEW.workflow_run_id USING ERRCODE = 'foreign_key_violation';
    END IF;
    run_is_terminal := run_status IN ('Success', 'Failure', 'Cancelled');
    IF TG_OP = 'INSERT' THEN
        IF NEW.generation <> 1 OR NEW.revision <> 1 THEN
            RAISE EXCEPTION 'workflow_run_data_coverage starts at generation 1 revision 1';
        END IF;
        IF cardinality(NEW.baseline_facets) <> (SELECT COUNT(DISTINCT facet) FROM unnest(NEW.baseline_facets) AS facet) THEN
            RAISE EXCEPTION 'workflow_run_data_coverage baseline facets must be unique';
        END IF;
        IF (NEW.state = 'Sealed') <> run_is_terminal THEN
            RAISE EXCEPTION 'workflow_run_data_coverage initial state must agree with workflow run terminality';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.id <> OLD.id OR NEW.team_id <> OLD.team_id OR NEW.workflow_run_id <> OLD.workflow_run_id
       OR NEW.baseline_facets IS DISTINCT FROM OLD.baseline_facets
       OR NEW.schema_version <> OLD.schema_version OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'workflow_run_data_coverage stable identity, schema and creation time are immutable';
    END IF;
    IF NEW.revision <> OLD.revision + 1 THEN
        RAISE EXCEPTION 'workflow_run_data_coverage revision must advance by exactly one';
    END IF;
    IF NEW.state = OLD.state THEN RAISE EXCEPTION 'workflow_run_data_coverage has no same-state rewrite'; END IF;
    IF OLD.state = 'Open' AND NEW.state = 'Sealed' THEN
        IF NOT run_is_terminal OR NEW.generation <> OLD.generation THEN
            RAISE EXCEPTION 'Open to Sealed requires a terminal run and preserves generation';
        END IF;
    ELSIF OLD.state = 'Sealed' AND NEW.state = 'Open' THEN
        IF run_is_terminal OR NEW.generation <> OLD.generation + 1 THEN
            RAISE EXCEPTION 'Sealed to Open requires a nonterminal run and advances generation exactly once';
        END IF;
    ELSE
        RAISE EXCEPTION 'unsupported workflow_run_data_coverage transition % to %', OLD.state, NEW.state;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- DML-touching trigger DDL is deliberately last. DbUp retains its relation lock until the upgrade transaction commits;
-- nothing after these statements scans an existing table or performs data work.
CREATE TRIGGER workflow_run_data_coverage_enforces_state_machine
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_data_coverage
    FOR EACH ROW EXECUTE FUNCTION workflow_run_data_coverage_guard();
CREATE TRIGGER workflow_run_data_coverage_facet_is_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_data_coverage_facet
    FOR EACH ROW EXECUTE FUNCTION workflow_run_data_coverage_facet_immutable();
CREATE TRIGGER workflow_run_data_coverage_follows_status
    AFTER UPDATE OF status ON workflow_run
    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
    EXECUTE FUNCTION workflow_run_data_coverage_follow_status();
CREATE TRIGGER workflow_run_data_manifest_admit_coverage
    BEFORE INSERT ON workflow_run_data_manifest
    FOR EACH ROW EXECUTE FUNCTION workflow_run_data_manifest_admit_coverage();
