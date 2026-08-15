-- 0126_storage_credential.sql
--
-- Additive provider-neutral storage credential ledger. `storage_credential` is the durable team-scoped identity/current
-- pointer; `storage_credential_revision` is immutable encrypted history for an arbitrary provider SecretSchema payload.
-- StorageProfile, ArtifactStore, API, harness and completion behavior are deliberately untouched in this slice.
--
-- Encryption boundary: encrypted_payload is the complete ciphertext envelope produced by the existing
-- IPayloadEncryptor/Data Protection convention. Data Protection embeds its key id in the protected envelope and resolves
-- the encryption algorithm through that key's shared key-ring descriptor. Separate key-version/algorithm columns would
-- duplicate self-describing envelope state and can drift, so they intentionally do not exist. envelope_fingerprint is
-- SHA-256 of the ciphertext envelope (never plaintext); safe_hint is optional pre-sanitized display metadata.
-- The stable UUID and immutable revision ordinal can later project to StorageSecretReference.SecretId/SecretVersion;
-- SecretStoreType remains a broker-owned type discriminator. No ciphertext or plaintext enters that opaque reference.
--
-- Rollback: DROP TABLE storage_credential_revision; DROP TABLE storage_credential;
--           DROP FUNCTION storage_credential_revision_guard(); DROP FUNCTION storage_credential_guard_identity();

CREATE TABLE storage_credential (
    id                  UUID         NOT NULL PRIMARY KEY,
    team_id             UUID         NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    stable_name         VARCHAR(128) NOT NULL,
    current_revision    INTEGER      NOT NULL,
    state               VARCHAR(16)  NOT NULL,
    created_date        TIMESTAMPTZ  NOT NULL,
    created_by          UUID         NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    revoked_date        TIMESTAMPTZ  NULL,
    revoked_by          UUID         NULL REFERENCES app_user(id) ON DELETE RESTRICT,

    CONSTRAINT ak_storage_credential_team_id UNIQUE (team_id, id),
    CONSTRAINT ck_storage_credential_current_revision CHECK (current_revision > 0),
    CONSTRAINT ck_storage_credential_revocation CHECK (
        (state = 'Active' AND revoked_date IS NULL AND revoked_by IS NULL)
        OR (state = 'Revoked' AND revoked_date IS NOT NULL AND revoked_by IS NOT NULL)
    ),
    CONSTRAINT ck_storage_credential_stable_name CHECK (stable_name ~ '^[a-z0-9][a-z0-9-]{0,127}$'),
    CONSTRAINT ck_storage_credential_state CHECK (state IN ('Active', 'Revoked'))
);

CREATE UNIQUE INDEX ux_storage_credential_team_stable_name ON storage_credential (team_id, stable_name);
CREATE INDEX ix_storage_credential_team_state ON storage_credential (team_id, state, stable_name);

CREATE TABLE storage_credential_revision (
    id                      UUID         NOT NULL PRIMARY KEY,
    team_id                 UUID         NOT NULL,
    storage_credential_id   UUID         NOT NULL,
    revision                INTEGER      NOT NULL,
    provider_type_key       VARCHAR(128) NOT NULL,
    encrypted_payload       TEXT         NOT NULL,
    safe_hint               VARCHAR(32)  NULL,
    envelope_fingerprint    VARCHAR(71)  NOT NULL,
    created_date            TIMESTAMPTZ  NOT NULL,
    created_by              UUID         NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,

    CONSTRAINT fk_storage_credential_revision_credential FOREIGN KEY (team_id, storage_credential_id)
        REFERENCES storage_credential (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_storage_credential_revision_encrypted_payload CHECK (btrim(encrypted_payload) <> ''),
    CONSTRAINT ck_storage_credential_revision_envelope_fingerprint CHECK (envelope_fingerprint ~ '^sha256:[0-9a-f]{64}$'),
    CONSTRAINT ck_storage_credential_revision_number CHECK (revision > 0),
    CONSTRAINT ck_storage_credential_revision_provider_type_key CHECK (provider_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'),
    CONSTRAINT ck_storage_credential_revision_safe_hint CHECK (
        safe_hint IS NULL
        OR (char_length(safe_hint) BETWEEN 1 AND 32 AND btrim(safe_hint) <> '' AND safe_hint !~ '[[:cntrl:]]')
    ),
    CONSTRAINT ux_storage_credential_revision_number UNIQUE (team_id, storage_credential_id, revision)
);

CREATE INDEX ix_storage_credential_revision_team_provider_created
    ON storage_credential_revision (team_id, provider_type_key, created_date, id);

COMMENT ON COLUMN storage_credential_revision.encrypted_payload IS
    'Complete IPayloadEncryptor/Data Protection ciphertext envelope for one provider SecretSchema payload; never plaintext.';
COMMENT ON COLUMN storage_credential_revision.safe_hint IS
    'Optional pre-sanitized display hint only; never used to authenticate or resolve provider configuration.';
COMMENT ON COLUMN storage_credential_revision.envelope_fingerprint IS
    'sha256 fingerprint of the encrypted envelope, allowing diagnostics without logging or indexing ciphertext.';

-- The pointer is a real tenant-bound reference. Deferred checking permits identity and revision one to be inserted in
-- one transaction without weakening the invariant at commit.
ALTER TABLE storage_credential
    ADD CONSTRAINT fk_storage_credential_current_revision
    FOREIGN KEY (team_id, id, current_revision)
    REFERENCES storage_credential_revision (team_id, storage_credential_id, revision)
    ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;

CREATE OR REPLACE FUNCTION storage_credential_revision_guard() RETURNS trigger AS $$
DECLARE
    credential_state VARCHAR(16);
    latest_revision INTEGER;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION
            'storage_credential_revision is immutable — % rejected (credential_id=%, revision=%). Append a new revision instead.',
            TG_OP, OLD.storage_credential_id, OLD.revision;
    END IF;

    -- Serialize append with revocation. If revocation commits first, this append fails; if append commits first, the
    -- revocation follows it and becomes the terminal boundary. The tenant-bound FK remains the final missing-parent guard.
    SELECT state INTO credential_state
    FROM storage_credential
    WHERE team_id = NEW.team_id AND id = NEW.storage_credential_id
    FOR UPDATE;

    IF FOUND AND credential_state = 'Revoked' THEN
        RAISE EXCEPTION 'storage_credential Revoked state is terminal — revision append rejected (id=%).', NEW.storage_credential_id;
    END IF;

    IF FOUND THEN
        SELECT COALESCE(MAX(revision), 0) INTO latest_revision
        FROM storage_credential_revision
        WHERE team_id = NEW.team_id AND storage_credential_id = NEW.storage_credential_id;

        IF NEW.revision <> latest_revision + 1 THEN
            RAISE EXCEPTION
                'storage_credential_revision is a contiguous append-only sequence (credential_id=%, latest=%, attempted=%).',
                NEW.storage_credential_id, latest_revision, NEW.revision;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_credential_revision_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON storage_credential_revision
    FOR EACH ROW EXECUTE FUNCTION storage_credential_revision_guard();

CREATE OR REPLACE FUNCTION storage_credential_guard_identity() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'storage_credential is durable identity — DELETE rejected (id=%). Revoke it instead.', OLD.id;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.stable_name IS DISTINCT FROM OLD.stable_name OR NEW.created_date IS DISTINCT FROM OLD.created_date
       OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'storage_credential stable identity is immutable (id=%).', OLD.id;
    END IF;

    IF NEW.current_revision < OLD.current_revision THEN
        RAISE EXCEPTION 'storage_credential current_revision is monotonic (id=%, old=%, new=%).', OLD.id, OLD.current_revision, NEW.current_revision;
    END IF;

    IF OLD.state = 'Revoked' AND (
        NEW.state IS DISTINCT FROM OLD.state OR NEW.current_revision IS DISTINCT FROM OLD.current_revision
        OR NEW.revoked_date IS DISTINCT FROM OLD.revoked_date OR NEW.revoked_by IS DISTINCT FROM OLD.revoked_by
    ) THEN
        RAISE EXCEPTION 'storage_credential Revoked state is terminal (id=%, attempted_state=%).', OLD.id, NEW.state;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_credential_guard_stable_identity
    BEFORE UPDATE OR DELETE ON storage_credential
    FOR EACH ROW EXECUTE FUNCTION storage_credential_guard_identity();
