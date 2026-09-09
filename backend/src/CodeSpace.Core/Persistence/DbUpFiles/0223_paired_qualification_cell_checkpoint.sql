-- Generic append-only runner checkpoints let recovery adopt durable work without repeating a paid launch.
CREATE TABLE paired_qualification_cell_checkpoint (
    id uuid PRIMARY KEY,
    admission_id uuid NOT NULL REFERENCES paired_qualification_cell_admission(id) ON DELETE RESTRICT,
    kind varchar(100) NOT NULL,
    payload_json jsonb NOT NULL,
    created_date timestamptz NOT NULL,
    created_by uuid NOT NULL,
    last_modified_date timestamptz NOT NULL,
    last_modified_by uuid NOT NULL,
    CONSTRAINT ck_paired_qualification_cell_checkpoint_shape CHECK (
        id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND btrim(kind) <> ''
        AND jsonb_typeof(payload_json) = 'object'
        AND octet_length(payload_json::text) <= 65536),
    CONSTRAINT uq_paired_qualification_cell_checkpoint_kind UNIQUE (admission_id, kind)
);

CREATE OR REPLACE FUNCTION paired_qualification_cell_checkpoint_reject_mutations() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'paired qualification cell checkpoint is append-only — % rejected (id=%)', TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER paired_qualification_cell_checkpoint_immutable
    BEFORE UPDATE OR DELETE ON paired_qualification_cell_checkpoint
    FOR EACH ROW EXECUTE FUNCTION paired_qualification_cell_checkpoint_reject_mutations();

COMMENT ON TABLE paired_qualification_cell_checkpoint IS 'Bounded append-only runner-owned recovery checkpoints. One immutable payload per admission and open checkpoint kind.';
