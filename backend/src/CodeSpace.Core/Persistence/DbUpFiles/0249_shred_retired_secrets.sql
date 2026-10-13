-- 0249_shred_retired_secrets.sql
--
-- A retired secret keeps no ciphertext. Until now four kinds of retirement left theirs decryptable forever, readable by
-- anyone holding a database copy and the key:
--
--   * deleting a git provider connection marked its credentials Revoked and soft-deleted, but kept each token in
--     credential.encrypted_payload, the connection's OAuth client secret in provider_instance.oauth_client_secret_enc,
--     and each group hook's HMAC secret in connection_webhook.secret_enc;
--   * deleting a variable kept a Secret's AES-GCM envelope in variable.value_encrypted, because
--     chk_variable_value_exclusive (0012) required one on every Secret row, deleted or not;
--   * revoking a storage credential kept every revision's envelope in storage_credential_revision.encrypted_payload,
--     because storage_credential_revision_guard (0126) refused every UPDATE;
--   * rotating or clearing a deployment storage default's secret repointed the template and kept the envelope it
--     replaced in storage_default_credential.encrypted_payload, because storage_default_credential_guard (0173) refused
--     every UPDATE.
--
-- The application now blanks each of these when it retires one. This script relaxes the three guards just enough to
-- let it, and blanks what retirement already left behind. It changes no live secret:
--
--   * credential: blanked only when Revoked or soft-deleted. Every other retirement path (Disconnect, unlinking an
--     identity, a departure) already blanked the payload; the rows left holding one are a deleted connection's. Nothing
--     reads a Revoked or soft-deleted credential's token: credential succession reads only ids and dates.
--   * provider_instance: the client secret is blanked only on a soft-deleted connection. Nothing restores one.
--   * connection_webhook: a soft-deleted connection's hooks are taken out of service (Cancelled) and their secrets
--     blanked. Ingestion refuses a Cancelled hook before it reads the secret, the registrar only ever claims an
--     Enqueued row, and a deleted connection's hooks could never be retired by the application: their registering
--     token is already blanked, so a hook still registered at the provider has to be removed there by hand. Its
--     external_id and owner_path stay, as the record of which hook that is.
--   * variable: a Secret may hold no ciphertext once deleted_date is set — and only then; a live Secret must still
--     carry one. Every read filters on deleted_date IS NULL and nothing restores a deleted variable.
--   * storage_credential_revision: exactly one UPDATE is admitted per revision — the one that blanks its payload and
--     changes nothing else — and only once its credential is Revoked. A Revoked credential resolves for nobody (the
--     secret resolver answers NotActive before it reads an envelope), revocation is terminal, and it is refused while a
--     stored location or a live profile still resolves through the credential. A superseded revision of an ACTIVE
--     credential is not blanked and may not be: a storage profile pins an exact revision (db:{id}:{revision}), so it
--     still decrypts the bytes stored through it, and routed bytes are permanent. A revision is never inserted blank.
--   * storage_default_credential: exactly one UPDATE is admitted per envelope — the one that blanks its payload and
--     changes nothing else — and only while no storage_default names it. The materializer is the only reader, and it
--     decrypts only the envelope its template names, read under a share lock on that template; an envelope is only
--     ever named by the template that appended it. Fingerprint and hint stay as history. An envelope is never inserted
--     blank.
--
-- Safe on a populated database: the new checks are weaker than the old ones, so every existing row passes them; the
-- backfills touch only retired rows. Never refuses.
--
-- Rollback. The blanked ciphertexts do not come back — that is the point — and so 0012's, 0126's and 0173's checks
-- cannot be put back as they were: once anything is shredded, by this script or by the application, a deleted Secret
-- variable holds NULL and a shredded revision or envelope holds '', exactly the rows those checks reject, so re-adding
-- them as written fails with 23514. Re-add them NOT VALID (enforced on every row written from then on, not on the rows
-- already shredded), and put 0173's trigger back on UPDATE and DELETE only:
--
--   ALTER TABLE variable DROP CONSTRAINT chk_variable_value_exclusive;
--   ALTER TABLE variable ADD CONSTRAINT chk_variable_value_exclusive CHECK ((value_type = 'Secret' AND value_encrypted IS NOT NULL AND value_plain IS NULL) OR (value_type <> 'Secret' AND value_plain IS NOT NULL AND value_encrypted IS NULL)) NOT VALID;
--   ALTER TABLE storage_credential_revision DROP CONSTRAINT ck_storage_credential_revision_encrypted_payload;
--   ALTER TABLE storage_credential_revision ADD CONSTRAINT ck_storage_credential_revision_encrypted_payload CHECK (btrim(encrypted_payload) <> '') NOT VALID;
--   ALTER TABLE storage_default_credential DROP CONSTRAINT ck_storage_default_credential_encrypted_payload;
--   ALTER TABLE storage_default_credential ADD CONSTRAINT ck_storage_default_credential_encrypted_payload CHECK (btrim(encrypted_payload) <> '') NOT VALID;
--   DROP TRIGGER storage_default_credential_enforce_append_only ON storage_default_credential;
--   CREATE TRIGGER storage_default_credential_enforce_append_only BEFORE UPDATE OR DELETE ON storage_default_credential FOR EACH ROW EXECUTE FUNCTION storage_default_credential_guard();
--
-- then re-run, verbatim, 0126's CREATE OR REPLACE FUNCTION storage_credential_revision_guard() block and 0173's
-- CREATE OR REPLACE FUNCTION storage_default_credential_guard() block. Restoring those functions is safe on any
-- database: a trigger judges only the rows written after it. The hooks this script cancelled stay cancelled.
--
-- Rolling the application back needs none of this: the previous build runs on this schema as it stands. Once the old
-- checks are back, though, this build cannot retire a secret — deleting a Secret variable fails with 23514 and revoking
-- a storage credential or editing a default's secret is refused — so before rolling forward again, delete this script's
-- journal row and let the next start apply it once more; every statement in it is safe to run twice:
--
--   DELETE FROM schemaversions WHERE scriptname = '0249_shred_retired_secrets.sql';

-- ── Variables ────────────────────────────────────────────────────────────────

ALTER TABLE variable DROP CONSTRAINT IF EXISTS chk_variable_value_exclusive;

ALTER TABLE variable ADD CONSTRAINT chk_variable_value_exclusive CHECK (
    (value_type =  'Secret' AND value_plain IS NULL AND (value_encrypted IS NOT NULL OR deleted_date IS NOT NULL))
    OR
    (value_type <> 'Secret' AND value_plain IS NOT NULL AND value_encrypted IS NULL)
);

UPDATE variable SET value_encrypted = NULL WHERE deleted_date IS NOT NULL AND value_encrypted IS NOT NULL;

-- ── Storage credential revisions ─────────────────────────────────────────────

ALTER TABLE storage_credential_revision DROP CONSTRAINT IF EXISTS ck_storage_credential_revision_encrypted_payload;

ALTER TABLE storage_credential_revision ADD CONSTRAINT ck_storage_credential_revision_encrypted_payload CHECK (encrypted_payload = '' OR btrim(encrypted_payload) <> '');

COMMENT ON COLUMN storage_credential_revision.encrypted_payload IS
    'Complete IPayloadEncryptor/Data Protection ciphertext envelope for one provider SecretSchema payload; never plaintext. Empty once the credential is Revoked (0249).';

CREATE OR REPLACE FUNCTION storage_credential_revision_guard() RETURNS trigger AS $$
DECLARE
    credential_state VARCHAR(16);
    latest_revision INTEGER;
BEGIN
    -- The one UPDATE a revision ever admits: its payload blanked, every other column as it was, its credential Revoked.
    -- Compared as whole rows minus the payload, so a column added later is covered too.
    IF TG_OP = 'UPDATE' THEN
        IF OLD.encrypted_payload <> '' AND NEW.encrypted_payload = ''
           AND to_jsonb(NEW) - 'encrypted_payload' = to_jsonb(OLD) - 'encrypted_payload' THEN
            SELECT state INTO credential_state
            FROM storage_credential
            WHERE team_id = OLD.team_id AND id = OLD.storage_credential_id;

            IF credential_state = 'Revoked' THEN
                RETURN NEW;
            END IF;

            RAISE EXCEPTION
                'storage_credential_revision payload may be blanked only once its credential is Revoked — a profile pins an exact revision, and an Active credential still decrypts it for the bytes stored through it (credential_id=%, revision=%).',
                OLD.storage_credential_id, OLD.revision;
        END IF;
    END IF;

    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION
            'storage_credential_revision is immutable — % rejected (credential_id=%, revision=%). Append a new revision instead.',
            TG_OP, OLD.storage_credential_id, OLD.revision;
    END IF;

    IF NEW.encrypted_payload = '' THEN
        RAISE EXCEPTION 'storage_credential_revision payload is required — only a revoked credential''s revision is ever blank (credential_id=%).', NEW.storage_credential_id;
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

UPDATE storage_credential_revision AS revision
   SET encrypted_payload = ''
  FROM storage_credential AS credential
 WHERE credential.team_id = revision.team_id
   AND credential.id = revision.storage_credential_id
   AND credential.state = 'Revoked'
   AND revision.encrypted_payload <> '';

-- ── Git provider credentials and connections ─────────────────────────────────

UPDATE credential SET encrypted_payload = '' WHERE encrypted_payload <> '' AND (deleted_date IS NOT NULL OR status = 'Revoked');

UPDATE provider_instance SET oauth_client_secret_enc = NULL WHERE deleted_date IS NOT NULL AND oauth_client_secret_enc IS NOT NULL;

UPDATE connection_webhook AS hook
   SET secret_enc = '', registration_status = 'Cancelled'
  FROM provider_instance AS instance
 WHERE instance.id = hook.provider_instance_id
   AND instance.deleted_date IS NOT NULL
   AND (hook.secret_enc <> '' OR hook.registration_status <> 'Cancelled');

-- ── Deployment storage default envelopes ─────────────────────────────────────

ALTER TABLE storage_default_credential DROP CONSTRAINT IF EXISTS ck_storage_default_credential_encrypted_payload;

ALTER TABLE storage_default_credential ADD CONSTRAINT ck_storage_default_credential_encrypted_payload CHECK (encrypted_payload = '' OR btrim(encrypted_payload) <> '');

COMMENT ON COLUMN storage_default_credential.encrypted_payload IS
    'Complete IPayloadEncryptor/Data Protection ciphertext envelope for one provider SecretSchema payload; never plaintext. Empty once no template names the envelope (0249).';

CREATE OR REPLACE FUNCTION storage_default_credential_guard() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.encrypted_payload = '' THEN
            RAISE EXCEPTION 'storage_default_credential payload is required -- only an envelope no template names is ever blank (id=%).', NEW.id;
        END IF;

        RETURN NEW;
    END IF;

    -- The one UPDATE an envelope ever admits: its payload blanked, every other column as it was, no template naming it.
    -- Compared as whole rows minus the payload, so a column added later is covered too.
    IF TG_OP = 'UPDATE'
       AND OLD.encrypted_payload <> '' AND NEW.encrypted_payload = ''
       AND to_jsonb(NEW) - 'encrypted_payload' = to_jsonb(OLD) - 'encrypted_payload' THEN
        IF NOT EXISTS (SELECT 1 FROM storage_default WHERE credential_id = OLD.id) THEN
            RETURN NEW;
        END IF;

        RAISE EXCEPTION
            'storage_default_credential payload may be blanked only once no template names it -- the materializer decrypts the envelope its template points at (id=%).',
            OLD.id;
    END IF;

    RAISE EXCEPTION
        'storage_default_credential is append-only -- % rejected (id=%). Insert a new envelope and repoint storage_default.credential_id.',
        TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS storage_default_credential_enforce_append_only ON storage_default_credential;

CREATE TRIGGER storage_default_credential_enforce_append_only
    BEFORE INSERT OR UPDATE OR DELETE ON storage_default_credential
    FOR EACH ROW EXECUTE FUNCTION storage_default_credential_guard();

UPDATE storage_default_credential AS envelope
   SET encrypted_payload = ''
 WHERE envelope.encrypted_payload <> ''
   AND NOT EXISTS (SELECT 1 FROM storage_default AS template WHERE template.credential_id = envelope.id);
