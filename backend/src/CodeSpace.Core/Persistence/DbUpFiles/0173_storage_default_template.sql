-- 0173_storage_default_template.sql
--
-- The DEPLOYMENT-WIDE storage template tier: one operator-authored row per routed data class describing where a team
-- SHOULD be pointed, plus the instance-scope ciphertext it needs and the provenance ledger for teams that have been
-- pointed there.
--
-- NOTHING READS THIS YET. No team resolves storage through it, no route is created from it, and no byte moves because
-- of it. The intended reader is the MATERIALIZER lane, which will turn one of these rows into a team's own
-- storage_credential + storage_profile + storage_route and record what it did in storage_default_materialization.
-- Until that lane ships, every row here is inert operator configuration. Do not describe this tier as something a
-- team "inherits" -- no team inherits anything from it today.
--
-- Rollback: DROP TABLE storage_default_materialization; DROP TABLE storage_default; DROP TABLE storage_default_credential;
--           DROP FUNCTION storage_default_guard_identity(); DROP FUNCTION storage_default_credential_guard();
--           DROP FUNCTION storage_default_materialization_guard();
--           DELETE FROM role_permission WHERE id = '00000000-0000-0000-0000-000000000023';
--           DELETE FROM permission WHERE id = '00000000-0000-0000-0000-000000000022';

-- ---------------------------------------------------------------------------------------------------------------
-- 1. The instance capability.
-- ---------------------------------------------------------------------------------------------------------------
--
-- Deployment defaults are not a fact about any team, so no TeamRole can express them -- the same reasoning that put
-- teams.create in this tier in 0115.
--
-- TWO rows, and both are load bearing for DIFFERENT readers:
--   * ENFORCEMENT reads role_permission OR user_permission (GlobalPermissionAuthorizationBehavior also lets the Admin
--     role through implicitly, so the permission row alone would already gate the write correctly).
--   * The /me PROJECTION (Services/Users/UserService.cs LoadInstancePermissionsAsync) has NO implicit-Admin branch --
--     it lists exactly the permissions reachable through role_permission or user_permission. Omit the role_permission
--     row below and the server accepts the write while /me never reports the capability, so a future admin UI hides
--     the control from the only account that holds it.
--
-- OPERATOR NOTE -- WHO CAN ACTUALLY HOLD THIS. In this build the answer is: the bootstrap admin seeded by
-- 0006_default_admin_seed.sql, and nobody else. role_user has no C# writer at all (the only INSERTs are in migrations
-- 0004 and 0006), and user_permission has exactly one writer (TeamInvitationService, which grants only the
-- Permissions.GrantedToEveryAccount list -- deliberately NOT this capability, since that list is handed to every
-- account that exists). There is therefore no product path to grant this to a second person; an authorization UI is
-- out of scope for this tier. An operator who needs a second deployment admin must INSERT a role_user or
-- user_permission row directly against the database.

INSERT INTO permission (id, name, display_name, description, is_system, created_date, created_by, last_modified_date, last_modified_by)
VALUES (
    '00000000-0000-0000-0000-000000000022',
    'storage.defaults.manage',
    'Manage deployment storage defaults',
    'May author the deployment-wide storage template every team is pointed at. Instance-level: the template describes all teams, so it is not an action inside any one of them.',
    true,
    now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001'
)
ON CONFLICT (id) DO NOTHING;

INSERT INTO role_permission (id, role_id, permission_id, created_date, created_by, last_modified_date, last_modified_by)
VALUES (
    '00000000-0000-0000-0000-000000000023',
    '00000000-0000-0000-0000-000000000010',
    '00000000-0000-0000-0000-000000000022',
    now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001'
)
ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------------------------------------------------------
-- 2. Instance-scope ciphertext.
-- ---------------------------------------------------------------------------------------------------------------
--
-- A deployment default carries real credentials, so the template needs somewhere to hold a secret that belongs to no
-- team. Same envelope convention as storage_credential_revision: the complete IPayloadEncryptor / Data Protection
-- envelope, which embeds its own key id and algorithm descriptor, so there are deliberately no key-version or
-- algorithm columns to drift. IPayloadEncryptor.Encrypt/Decrypt take no team, so instance-scope ciphertext is
-- representable with the primitive already in the build.
--
-- Append-only. Rotating the template's secret INSERTs a new row and repoints storage_default.credential_id; the
-- superseded envelope stays as history rather than being overwritten in place.

CREATE TABLE storage_default_credential (
    id                   UUID         NOT NULL PRIMARY KEY,
    provider_type_key    VARCHAR(128) NOT NULL,
    encrypted_payload    TEXT         NOT NULL,
    safe_hint            VARCHAR(32)  NULL,
    envelope_fingerprint VARCHAR(71)  NOT NULL,
    created_date         TIMESTAMPTZ  NOT NULL,
    created_by           UUID         NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,

    CONSTRAINT ck_storage_default_credential_encrypted_payload CHECK (btrim(encrypted_payload) <> ''),
    CONSTRAINT ck_storage_default_credential_envelope_fingerprint CHECK (envelope_fingerprint ~ '^sha256:[0-9a-f]{64}$'),
    CONSTRAINT ck_storage_default_credential_provider_type_key CHECK (provider_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'),
    CONSTRAINT ck_storage_default_credential_safe_hint CHECK (
        safe_hint IS NULL
        OR (char_length(safe_hint) BETWEEN 1 AND 32 AND btrim(safe_hint) <> '' AND safe_hint !~ '[[:cntrl:]]')
    )
);

CREATE INDEX ix_storage_default_credential_provider_created
    ON storage_default_credential (provider_type_key, created_date, id);

COMMENT ON TABLE storage_default_credential IS
    'Instance-scope (team-less) ciphertext for the deployment storage template. Append-only. No runtime path reads it yet; the materializer lane is the intended reader.';
COMMENT ON COLUMN storage_default_credential.encrypted_payload IS
    'Complete IPayloadEncryptor/Data Protection ciphertext envelope for one provider SecretSchema payload; never plaintext.';
COMMENT ON COLUMN storage_default_credential.envelope_fingerprint IS
    'sha256 fingerprint of the encrypted envelope, allowing diagnostics without logging or indexing ciphertext.';

-- ---------------------------------------------------------------------------------------------------------------
-- 3. The template itself.
-- ---------------------------------------------------------------------------------------------------------------

CREATE TABLE storage_default (
    id                   UUID         NOT NULL PRIMARY KEY,
    data_class_type_key  VARCHAR(128) NOT NULL,
    revision             INTEGER      NOT NULL,
    provider_type_key    VARCHAR(128) NOT NULL,
    config_jsonb         JSONB        NOT NULL,
    namespace_root       VARCHAR(512) NOT NULL,
    credential_id        UUID         NULL REFERENCES storage_default_credential(id) ON DELETE RESTRICT,
    adoption_policy      VARCHAR(16)  NOT NULL,
    is_enabled           BOOLEAN      NOT NULL,
    created_date         TIMESTAMPTZ  NOT NULL,
    created_by           UUID         NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    last_modified_date   TIMESTAMPTZ  NOT NULL,
    last_modified_by     UUID         NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,

    CONSTRAINT ck_storage_default_adoption_policy CHECK (adoption_policy IN ('Automatic', 'Explicit')),
    CONSTRAINT ck_storage_default_config_object CHECK (jsonb_typeof(config_jsonb) = 'object'),
    CONSTRAINT ck_storage_default_data_class_type_key CHECK (data_class_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'),
    CONSTRAINT ck_storage_default_namespace_root CHECK (btrim(namespace_root) <> '' AND namespace_root !~ '[[:cntrl:]]'),
    CONSTRAINT ck_storage_default_provider_type_key CHECK (provider_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'),
    CONSTRAINT ck_storage_default_revision CHECK (revision > 0)
);

CREATE UNIQUE INDEX ux_storage_default_data_class ON storage_default (data_class_type_key);

COMMENT ON TABLE storage_default IS
    'One operator-authored deployment default per routed data class. Nothing consumes it yet -- the materializer lane is the intended reader.';

-- THE MOST DANGEROUS COLUMN IN THIS FILE. It is a ROOT, not a namespace, and the materializer MUST append a
-- per-team segment to it before writing it into a team's storage_profile revision.
--
-- Why: ArtifactStore.Routing.cs ObjectKeyFor(sha256) builds 'workflow-artifacts/<aa>/<bb>/<sha256>' and contains NO
-- team segment. Tenant isolation therefore rests ENTIRELY on each team's profile namespace differing. Purge is
-- strictly per-team and deletes by the row's own ETag, and identical bytes produce an identical object with an
-- identical ETag -- so if two teams ever shared a namespace, one team's reaper would delete an object that another
-- team's artifact_location still marks Available. Silent cross-team data loss, with no error anywhere.
COMMENT ON COLUMN storage_default.namespace_root IS
    'ROOT ONLY -- never a finished namespace. The materializer MUST append a per-team segment before this reaches a storage_profile revision. Object keys carry no team segment (ArtifactStore.Routing.cs ObjectKeyFor), so two teams sharing a namespace means one team''s reaper deletes another team''s live objects.';

COMMENT ON COLUMN storage_default.config_jsonb IS
    'Provider config EXCLUDING every namespace field; the namespace is assembled from namespace_root plus the per-team segment the materializer appends.';

-- The adoption policy is a first-class column rather than a boolean and rather than knowledge the materializer holds,
-- so a data class added later CANNOT be routed without stating how it is adopted.
--
--   Automatic -- a team materializes this class on first write, the way AgentRunLogStorageReadiness already
--                bootstraps a local route today. Only safe for a class that has nowhere else to put its bytes: it is
--                refusing writes until it is cut over, so cutting it over takes nothing away.
--
--   Explicit  -- materialized ONLY when that team's admin adopts it. Never automatic.
--
-- WHY EXPLICIT EXISTS, in the terms that matter: once a team's route for a class is Active, that team is PERMANENTLY
-- off local disk for that class. StorageRouteRules.EnsureTransition refuses any transition back to Draft, Retired is
-- terminal, and a route cannot be deleted. "Overridable" here means "can be repointed at another destination", NOT
-- "can be returned to local". Auto-adopting that would commit every new team irreversibly without anyone choosing it.
--
-- The per-class restriction that follows from this -- a class that HAS a local home must never be Automatic -- is
-- enforced in the control plane (StorageDefaultRules), not here, because it is derived from a build-time declaration
-- (IRoutedDataClassLocalFallback) that SQL cannot see. This CHECK pins the vocabulary; the service pins which member
-- of it each class may use.
COMMENT ON COLUMN storage_default.adoption_policy IS
    'Automatic = materialized on a team''s first write (only for a class with no local home). Explicit = only on that team''s admin adopting it, because an Active route is permanently off local disk for that class -- EnsureTransition refuses a return to Draft and Retired is terminal.';

COMMENT ON COLUMN storage_default.revision IS
    'Monotonic edit counter, stamped into storage_default_materialization.source_revision so a materialized team can be told apart from a stale one. The template is not an append-only ledger because nothing durable pins a template revision -- the byte-exact content a team received is preserved in the immutable storage_profile_revision the materializer produces.';

CREATE OR REPLACE FUNCTION storage_default_guard_identity() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'storage_default is durable deployment configuration -- DELETE rejected (data_class=%). Disable it instead.', OLD.data_class_type_key;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.data_class_type_key IS DISTINCT FROM OLD.data_class_type_key
       OR NEW.created_date IS DISTINCT FROM OLD.created_date OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'storage_default stable identity is immutable (id=%).', OLD.id;
    END IF;

    IF NEW.revision < OLD.revision THEN
        RAISE EXCEPTION 'storage_default revision is monotonic (id=%, old=%, new=%).', OLD.id, OLD.revision, NEW.revision;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_default_enforce_identity
    BEFORE UPDATE OR DELETE ON storage_default
    FOR EACH ROW EXECUTE FUNCTION storage_default_guard_identity();

CREATE OR REPLACE FUNCTION storage_default_credential_guard() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION
        'storage_default_credential is append-only -- % rejected (id=%). Insert a new envelope and repoint storage_default.credential_id.',
        TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_default_credential_enforce_append_only
    BEFORE UPDATE OR DELETE ON storage_default_credential
    FOR EACH ROW EXECUTE FUNCTION storage_default_credential_guard();

-- ---------------------------------------------------------------------------------------------------------------
-- 4. Provenance -- written by the NEXT lane.
-- ---------------------------------------------------------------------------------------------------------------
--
-- THIS LANE CREATES THE TABLE; THE MATERIALIZER FILLS IT. Nothing in this build inserts a row here.
--
-- It doubles as the record of EXPLICIT ADOPTION: for an Explicit class, the presence of a row for (team_id,
-- data_class_type_key) is exactly what "this team adopted it" means, which is why the unique key is that pair and not
-- the surrogate id. One team adopts one data class once; a later re-materialization updates the row it already owns
-- rather than appending a second claim, and source_revision is what says which template edit it came from.
--
-- The profile reference is the tenant-bound composite (team_id, storage_profile_id) so a materialization can never
-- record a profile belonging to another team.

CREATE TABLE storage_default_materialization (
    id                  UUID         NOT NULL PRIMARY KEY,
    team_id             UUID         NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    data_class_type_key VARCHAR(128) NOT NULL,
    storage_profile_id  UUID         NOT NULL,
    source_revision     INTEGER      NOT NULL,
    created_date        TIMESTAMPTZ  NOT NULL,
    created_by          UUID         NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,

    CONSTRAINT fk_storage_default_materialization_profile FOREIGN KEY (team_id, storage_profile_id)
        REFERENCES storage_profile (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_storage_default_materialization_data_class_type_key CHECK (data_class_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'),
    CONSTRAINT ck_storage_default_materialization_source_revision CHECK (source_revision > 0),
    CONSTRAINT ux_storage_default_materialization_team_class UNIQUE (team_id, data_class_type_key)
);

CREATE INDEX ix_storage_default_materialization_class_created
    ON storage_default_materialization (data_class_type_key, created_date, id);

COMMENT ON TABLE storage_default_materialization IS
    'Provenance for teams pointed at the deployment default. Created by the template lane and written by the MATERIALIZER lane -- no code in this build inserts a row. For an Explicit data class its presence for (team_id, data_class_type_key) IS that team''s adoption.';
COMMENT ON COLUMN storage_default_materialization.source_revision IS
    'The storage_default.revision this team was materialized from; compare against the template''s current revision to find teams running a stale copy.';

CREATE OR REPLACE FUNCTION storage_default_materialization_guard() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION
            'storage_default_materialization is an adoption record -- DELETE rejected (team=%, data_class=%). A team cannot un-adopt: an Active route is permanently off local disk for that class.',
            OLD.team_id, OLD.data_class_type_key;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.data_class_type_key IS DISTINCT FROM OLD.data_class_type_key
       OR NEW.created_date IS DISTINCT FROM OLD.created_date OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'storage_default_materialization identity is immutable (team=%, data_class=%).', OLD.team_id, OLD.data_class_type_key;
    END IF;

    IF NEW.source_revision < OLD.source_revision THEN
        RAISE EXCEPTION
            'storage_default_materialization source_revision is monotonic (team=%, data_class=%, old=%, new=%).',
            OLD.team_id, OLD.data_class_type_key, OLD.source_revision, NEW.source_revision;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_default_materialization_enforce_invariants
    BEFORE UPDATE OR DELETE ON storage_default_materialization
    FOR EACH ROW EXECUTE FUNCTION storage_default_materialization_guard();
