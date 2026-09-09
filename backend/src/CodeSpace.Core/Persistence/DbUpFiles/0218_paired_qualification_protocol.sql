-- Immutable pre-registration for a paired qualification campaign. It is inserted and committed before the first
-- paid TaskLaunch cell so suite/model/statistical choices cannot be selected after outcomes are visible.
CREATE TABLE paired_qualification_protocol (
    observation_group_id uuid PRIMARY KEY,
    team_id uuid NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    suite_digest text NOT NULL,
    suite_version text NOT NULL,
    code_revision varchar(64) NOT NULL,
    -- Historical identities, deliberately soft references: credential retirement must not erase old evidence.
    control_model_row_id uuid NOT NULL,
    candidate_model_row_id uuid NOT NULL,
    statistics_version varchar(80) NOT NULL,
    criterion varchar(20) NOT NULL,
    sessions_per_cell integer NOT NULL,
    minimum_independent_clusters integer NOT NULL,
    minimum_strata integer NOT NULL,
    minimum_required_execution_clusters integer NOT NULL,
    minimum_evaluator_health double precision NOT NULL,
    max_cost_usd_per_launch numeric(18,6) NOT NULL,
    minimum_quality_lift double precision NOT NULL,
    non_inferiority_margin double precision NOT NULL,
    minimum_cost_reduction double precision NOT NULL,
    require_distinct_observed_models boolean NOT NULL,
    ordering_seed text NOT NULL,
    protocol_digest varchar(64) NOT NULL UNIQUE,
    created_date timestamptz NOT NULL,
    created_by uuid NOT NULL,
    last_modified_date timestamptz NOT NULL,
    last_modified_by uuid NOT NULL,
    CONSTRAINT ck_paired_qualification_protocol_shape CHECK (
        observation_group_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND control_model_row_id <> candidate_model_row_id
        AND btrim(suite_digest) <> '' AND btrim(suite_version) <> ''
        AND code_revision ~ '^[0-9A-Fa-f]{40}([0-9A-Fa-f]{24})?$'
        AND btrim(statistics_version) <> ''
        AND criterion IN ('Quality', 'Efficiency')
        AND sessions_per_cell >= 1 AND minimum_independent_clusters >= 1 AND minimum_strata >= 1
        AND minimum_required_execution_clusters >= 0
        AND minimum_evaluator_health BETWEEN 0 AND 1
        AND max_cost_usd_per_launch > 0
        AND minimum_quality_lift BETWEEN -1 AND 1
        AND non_inferiority_margin BETWEEN -1 AND 1
        AND minimum_cost_reduction BETWEEN 0 AND 1
        AND btrim(ordering_seed) <> ''
        AND protocol_digest ~ '^[0-9A-F]{64}$')
);

CREATE INDEX ix_paired_qualification_protocol_team_created ON paired_qualification_protocol (team_id, created_date);

CREATE OR REPLACE FUNCTION paired_qualification_protocol_reject_mutations() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'paired qualification protocol is immutable — % rejected (observation_group_id=%)', TG_OP, OLD.observation_group_id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER paired_qualification_protocol_immutable
    BEFORE UPDATE OR DELETE ON paired_qualification_protocol
    FOR EACH ROW EXECUTE FUNCTION paired_qualification_protocol_reject_mutations();

COMMENT ON TABLE paired_qualification_protocol IS 'Immutable protocol committed before any paid paired qualification cell; outcome observations join by observation_group_id.';
COMMENT ON COLUMN paired_qualification_protocol.protocol_digest IS 'SHA-256 of the canonical pre-outcome protocol fields, including exact suite, revision, model rows, criterion, thresholds, and ordering seed.';
