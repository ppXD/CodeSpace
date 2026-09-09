-- Immutable terminal seal over the pre-registered protocol, exact observation census, and derived statistics.
ALTER TABLE paired_qualification_protocol ADD CONSTRAINT uq_paired_qualification_protocol_result_binding
    UNIQUE (observation_group_id, protocol_digest, statistics_version);

CREATE TABLE paired_qualification_result (
    observation_group_id uuid PRIMARY KEY,
    protocol_digest varchar(64) NOT NULL UNIQUE,
    evidence_digest varchar(64) NOT NULL UNIQUE,
    result_digest varchar(64) NOT NULL UNIQUE,
    statistics_version varchar(80) NOT NULL,
    expected_observation_count integer NOT NULL,
    observation_count integer NOT NULL,
    qualified_for_capability_claim boolean NOT NULL,
    outcome_json text NOT NULL,
    created_date timestamptz NOT NULL,
    created_by uuid NOT NULL,
    last_modified_date timestamptz NOT NULL,
    last_modified_by uuid NOT NULL,
    CONSTRAINT ck_paired_qualification_result_shape CHECK (
        protocol_digest ~ '^[0-9A-F]{64}$'
        AND evidence_digest ~ '^[0-9A-F]{64}$'
        AND result_digest ~ '^[0-9A-F]{64}$'
        AND btrim(statistics_version) <> ''
        AND expected_observation_count > 0
        AND observation_count = expected_observation_count
        AND jsonb_typeof(outcome_json::jsonb) = 'object'
        AND outcome_json::jsonb ->> 'protocolDigest' = protocol_digest
        AND outcome_json::jsonb ->> 'evidenceDigest' = evidence_digest
        AND (outcome_json::jsonb ->> 'observationGroupId')::uuid = observation_group_id
        AND ((outcome_json::jsonb ->> 'pairedCells')::integer * 2) = expected_observation_count
        AND (outcome_json::jsonb ->> 'qualifiedForCapabilityClaim')::boolean = qualified_for_capability_claim),
    CONSTRAINT fk_paired_qualification_result_protocol FOREIGN KEY (observation_group_id, protocol_digest, statistics_version)
        REFERENCES paired_qualification_protocol(observation_group_id, protocol_digest, statistics_version) ON DELETE RESTRICT
);

CREATE OR REPLACE FUNCTION paired_qualification_result_reject_mutations() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'paired qualification result is immutable — % rejected (observation_group_id=%)', TG_OP, OLD.observation_group_id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER paired_qualification_result_immutable
    BEFORE UPDATE OR DELETE ON paired_qualification_result
    FOR EACH ROW EXECUTE FUNCTION paired_qualification_result_reject_mutations();

CREATE OR REPLACE FUNCTION benchmark_result_reject_sealed_group_append() RETURNS trigger AS $$
BEGIN
    IF NEW.observation_group_id IS NOT NULL AND EXISTS (
        SELECT 1 FROM paired_qualification_result result WHERE result.observation_group_id = NEW.observation_group_id) THEN
        RAISE EXCEPTION 'paired qualification result is sealed — observation append rejected (observation_group_id=%)', NEW.observation_group_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER benchmark_result_reject_sealed_group_append
    BEFORE INSERT ON benchmark_result
    FOR EACH ROW EXECUTE FUNCTION benchmark_result_reject_sealed_group_append();

COMMENT ON TABLE paired_qualification_result IS 'Immutable terminal result bound to one protocol and the exact append-only observation set it reduced.';
COMMENT ON COLUMN paired_qualification_result.evidence_digest IS 'SHA-256 over every immutable observation field in canonical session/task/mode/arm order.';
COMMENT ON COLUMN paired_qualification_result.result_digest IS 'SHA-256 over protocol digest, evidence digest, statistics version, and canonical outcome JSON.';
