-- 0125_storage_profile.sql
--
-- Additive provider-neutral storage profile ledger. `storage_profile` is the stable team-scoped identity/current
-- pointer; `storage_profile_revision` is immutable configuration history. Provider-specific runtime activation and
-- the existing ArtifactStore are deliberately untouched in this slice.
--
-- Security boundary: config_jsonb is NON-SECRET provider configuration; credential_ref is an opaque reference to a
-- separately protected credential. No plaintext key/token/secret column exists. namespace_fingerprint is SHA-256 so
-- endpoint/account/container/bucket/prefix identity can be compared without retaining that namespace in an index.
--
-- Rollback: DROP TABLE storage_profile CASCADE; DROP TABLE storage_profile_revision CASCADE;
--           DROP FUNCTION storage_profile_revision_reject_mutations(); DROP FUNCTION storage_profile_guard_identity();

CREATE TABLE storage_profile (
    id                  UUID        NOT NULL PRIMARY KEY,
    team_id             UUID        NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    stable_name         VARCHAR(128) NOT NULL,
    current_revision    INTEGER     NOT NULL,
    state               VARCHAR(16) NOT NULL,
    created_date        TIMESTAMPTZ NOT NULL,
    created_by          UUID        NOT NULL,
    last_modified_date  TIMESTAMPTZ NOT NULL,
    last_modified_by    UUID        NOT NULL,

    CONSTRAINT ak_storage_profile_team_id UNIQUE (team_id, id),
    CONSTRAINT ck_storage_profile_current_revision CHECK (current_revision > 0),
    CONSTRAINT ck_storage_profile_stable_name CHECK (stable_name ~ '^[a-z0-9][a-z0-9-]{0,127}$'),
    CONSTRAINT ck_storage_profile_state CHECK (state IN ('Draft', 'Active', 'Disabled', 'Retired'))
);

CREATE UNIQUE INDEX ux_storage_profile_team_stable_name ON storage_profile (team_id, stable_name);
CREATE INDEX ix_storage_profile_team_state ON storage_profile (team_id, state, stable_name);

CREATE TABLE storage_profile_revision (
    id                      UUID         NOT NULL PRIMARY KEY,
    team_id                 UUID         NOT NULL,
    storage_profile_id      UUID         NOT NULL,
    revision                INTEGER      NOT NULL,
    provider_type_key       VARCHAR(128) NOT NULL,
    config_jsonb            JSONB        NOT NULL,
    credential_ref          VARCHAR(512) NULL,
    namespace_fingerprint   VARCHAR(71)  NOT NULL,
    created_date            TIMESTAMPTZ  NOT NULL,
    created_by              UUID         NOT NULL,

    CONSTRAINT fk_storage_profile_revision_profile FOREIGN KEY (team_id, storage_profile_id)
        REFERENCES storage_profile (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_storage_profile_revision_config_object CHECK (jsonb_typeof(config_jsonb) = 'object'),
    CONSTRAINT ck_storage_profile_revision_credential_ref CHECK (credential_ref IS NULL OR btrim(credential_ref) <> ''),
    CONSTRAINT ck_storage_profile_revision_namespace_fingerprint CHECK (namespace_fingerprint ~ '^sha256:[0-9a-f]{64}$'),
    CONSTRAINT ck_storage_profile_revision_number CHECK (revision > 0),
    CONSTRAINT ck_storage_profile_revision_provider_type_key CHECK (provider_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'),
    CONSTRAINT ux_storage_profile_revision_number UNIQUE (team_id, storage_profile_id, revision)
);

CREATE INDEX ix_storage_profile_revision_team_provider_created
    ON storage_profile_revision (team_id, provider_type_key, created_date, id);

COMMENT ON COLUMN storage_profile_revision.config_jsonb IS
    'NON-SECRET provider configuration only; values declared by the provider SecretSchema must never be stored here.';
COMMENT ON COLUMN storage_profile_revision.credential_ref IS
    'Opaque reference to separately protected credential material; never a plaintext key, token, or secret.';
COMMENT ON COLUMN storage_profile_revision.namespace_fingerprint IS
    'sha256 fingerprint of endpoint/account/container/bucket/prefix identity; never the plaintext namespace.';

-- The pointer is a real tenant-bound reference. Deferred checking permits the profile and revision 1 to be inserted
-- in one transaction without weakening the invariant at commit.
ALTER TABLE storage_profile
    ADD CONSTRAINT fk_storage_profile_current_revision
    FOREIGN KEY (team_id, id, current_revision)
    REFERENCES storage_profile_revision (team_id, storage_profile_id, revision)
    ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;

CREATE OR REPLACE FUNCTION storage_profile_revision_reject_mutations() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION
        'storage_profile_revision is immutable — % rejected (profile_id=%, revision=%). Append a new revision instead.',
        TG_OP, OLD.storage_profile_id, OLD.revision;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_profile_revision_enforce_immutability
    BEFORE UPDATE OR DELETE ON storage_profile_revision
    FOR EACH ROW EXECUTE FUNCTION storage_profile_revision_reject_mutations();

CREATE OR REPLACE FUNCTION storage_profile_guard_identity() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'storage_profile is durable identity — DELETE rejected (id=%). Move state to Retired instead.', OLD.id;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.stable_name IS DISTINCT FROM OLD.stable_name OR NEW.created_date IS DISTINCT FROM OLD.created_date
       OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'storage_profile stable identity is immutable (id=%).', OLD.id;
    END IF;

    IF NEW.current_revision < OLD.current_revision THEN
        RAISE EXCEPTION 'storage_profile current_revision is monotonic (id=%, old=%, new=%).', OLD.id, OLD.current_revision, NEW.current_revision;
    END IF;

    IF OLD.state = 'Retired' AND NEW.state <> 'Retired' THEN
        RAISE EXCEPTION 'storage_profile Retired state is terminal (id=%, attempted_state=%).', OLD.id, NEW.state;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_profile_guard_stable_identity
    BEFORE UPDATE OR DELETE ON storage_profile
    FOR EACH ROW EXECUTE FUNCTION storage_profile_guard_identity();
