-- 0186_legacy_placement_adoption_arc.sql
--
-- Phase-two legacy placement adoption needs one population that remains closed across HTTP calls. workflow_artifact
-- ids are random and created_at is stamped by application pods, so neither is an insertion watermark: a slow-clock
-- writer can commit behind an already-issued (created_at,id) cursor and be skipped by Evidence or enter Minting
-- without having supplied evidence. These two tables materialize exactly the source identities visible to ONE SQL
-- statement. They are control-plane work state only: no FK points from workflow_artifact, no reader follows them, and
-- no immutable artifact row is updated.
--
-- At most one Active/Cleaning arc exists per team, so trying several profiles cannot copy an N-row legacy population
-- N times. Members have a DB identity used only as an immutable keyset; INSERT ... SELECT explicitly orders sources
-- before allocating it. Minting deletes only a successfully advanced bounded page, retaining its evidence witness
-- until the terminal commit. Evidence retains every member; an evidence refusal or expired/stale arc enters Cleaning,
-- whose lease-fenced calls remove only one bounded page before leaving the compact terminal tombstone.
--
-- Deployment cost, stated honestly: this migration creates empty tables and indexes and does not scan or lock
-- workflow_artifact. The O(N) copy happens only when an operator starts an adoption arc, as one set-based statement
-- under MVCC; it reads that team's offloaded legacy rows but does not block their INSERTs. DbUp still runs this DDL in
-- one transaction. Rollback: DROP TABLE legacy_placement_adoption_member; DROP TABLE legacy_placement_adoption_arc;
-- DROP FUNCTION legacy_placement_adoption_require_committed_shape();
-- DROP FUNCTION legacy_placement_adoption_member_guard_delete();
-- DROP FUNCTION legacy_placement_adoption_member_guard_insert();
-- DROP FUNCTION legacy_placement_adoption_member_reject_update(); DROP FUNCTION legacy_placement_adoption_arc_guard().

CREATE TABLE legacy_placement_adoption_arc (
    id                           UUID        NOT NULL PRIMARY KEY,
    team_id                      UUID        NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    storage_profile_id           UUID        NOT NULL,
    storage_profile_revision_id  UUID        NOT NULL,
    profile_revision             INTEGER     NOT NULL,
    created_by                   UUID        NOT NULL,
    phase                        VARCHAR(16) NOT NULL,
    state                        VARCHAR(16) NOT NULL,
    terminal_state               VARCHAR(16) NULL,
    witness_workflow_artifact_id UUID        NULL,
    current_position             BIGINT      NOT NULL DEFAULT 0,
    member_count                 BIGINT      NOT NULL DEFAULT 0,
    revision                     BIGINT      NOT NULL DEFAULT 1,
    claim_token                  UUID        NULL,
    claim_expires_at             TIMESTAMPTZ NULL,
    sealed_at                    TIMESTAMPTZ NULL,
    created_at                   TIMESTAMPTZ NOT NULL,
    last_modified_at             TIMESTAMPTZ NOT NULL,
    expires_at                   TIMESTAMPTZ NOT NULL,
    completed_at                 TIMESTAMPTZ NULL,
    final_summary_jsonb          JSONB       NULL,

    CONSTRAINT fk_legacy_adoption_arc_profile FOREIGN KEY (team_id, storage_profile_id)
        REFERENCES storage_profile(team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_legacy_adoption_arc_revision FOREIGN KEY (team_id, storage_profile_revision_id)
        REFERENCES storage_profile_revision(team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_legacy_adoption_arc_phase CHECK (phase IN ('Evidence', 'Minting', 'Cleaning')),
    CONSTRAINT ck_legacy_adoption_arc_state CHECK (state IN ('Active', 'Cleaning', 'Completed', 'Expired', 'Stale')),
    CONSTRAINT ck_legacy_adoption_arc_terminal CHECK (
        (state = 'Active' AND terminal_state IS NULL AND completed_at IS NULL AND final_summary_jsonb IS NULL)
        OR (state = 'Cleaning' AND terminal_state IN ('Completed', 'Expired', 'Stale') AND completed_at IS NULL
            AND final_summary_jsonb IS NOT NULL)
        OR (state IN ('Completed', 'Expired', 'Stale') AND terminal_state = state AND completed_at IS NOT NULL
            AND final_summary_jsonb IS NOT NULL)),
    CONSTRAINT ck_legacy_adoption_arc_phase_state CHECK (
        (state = 'Active' AND phase IN ('Evidence', 'Minting'))
        OR (state = 'Cleaning' AND phase = 'Cleaning')
        OR state IN ('Completed', 'Expired', 'Stale')),
    CONSTRAINT ck_legacy_adoption_arc_witness CHECK (
        phase = 'Evidence' OR (phase = 'Minting' AND witness_workflow_artifact_id IS NOT NULL) OR phase = 'Cleaning'),
    CONSTRAINT ck_legacy_adoption_arc_final_summary_object CHECK (
        final_summary_jsonb IS NULL OR jsonb_typeof(final_summary_jsonb) = 'object'),
    CONSTRAINT ck_legacy_adoption_arc_bounds CHECK (
        profile_revision > 0 AND current_position >= 0 AND member_count >= 0 AND revision > 0
        AND expires_at > created_at AND last_modified_at >= created_at
        AND (completed_at IS NULL OR completed_at >= created_at)
        AND (claim_token IS NULL) = (claim_expires_at IS NULL)),
    CONSTRAINT ck_legacy_adoption_arc_sealed CHECK (sealed_at IS NULL OR sealed_at >= created_at)
);

CREATE UNIQUE INDEX ux_legacy_placement_adoption_arc_team_live
    ON legacy_placement_adoption_arc (team_id)
    WHERE state IN ('Active', 'Cleaning');

CREATE INDEX ix_legacy_placement_adoption_arc_terminal_cleanup
    ON legacy_placement_adoption_arc (state, completed_at, id)
    WHERE state IN ('Completed', 'Expired', 'Stale');

CREATE TABLE legacy_placement_adoption_member (
    arc_id                UUID        NOT NULL REFERENCES legacy_placement_adoption_arc(id) ON DELETE CASCADE,
    position              BIGINT      GENERATED ALWAYS AS IDENTITY,
    workflow_artifact_id  UUID        NOT NULL,
    source_created_at     TIMESTAMPTZ NOT NULL,
    sha256                TEXT        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes            BIGINT      NOT NULL CHECK (size_bytes >= 0),
    storage_url           TEXT        NOT NULL CHECK (btrim(storage_url) <> ''),
    PRIMARY KEY (arc_id, position),
    CONSTRAINT ux_legacy_placement_adoption_member_source UNIQUE (arc_id, workflow_artifact_id)
);

CREATE OR REPLACE FUNCTION legacy_placement_adoption_arc_guard() RETURNS TRIGGER AS $$
DECLARE
    materialized_members BIGINT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.state NOT IN ('Completed', 'Expired', 'Stale') OR OLD.claim_token IS NOT NULL THEN
            RAISE EXCEPTION 'only an unclaimed terminal legacy adoption tombstone may be deleted (arc=%, state=%).', OLD.id, OLD.state;
        END IF;
        IF EXISTS (SELECT 1 FROM legacy_placement_adoption_member member WHERE member.arc_id = OLD.id) THEN
            RAISE EXCEPTION 'legacy adoption tombstone cannot be deleted while membership remains (arc=%).', OLD.id;
        END IF;
        RETURN OLD;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM storage_profile_revision profile_revision
        WHERE profile_revision.team_id = NEW.team_id
          AND profile_revision.storage_profile_id = NEW.storage_profile_id
          AND profile_revision.id = NEW.storage_profile_revision_id
          AND profile_revision.revision = NEW.profile_revision
    ) THEN
        RAISE EXCEPTION 'legacy adoption arc requires one exact profile revision (arc=%, profile=%, revision_id=%, revision=%).',
            NEW.id, NEW.storage_profile_id, NEW.storage_profile_revision_id, NEW.profile_revision;
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.revision <> 1 OR NEW.state <> 'Active' OR NEW.phase <> 'Evidence'
           OR NEW.terminal_state IS NOT NULL OR NEW.witness_workflow_artifact_id IS NOT NULL
           OR NEW.current_position <> 0 OR NEW.member_count <> 0
           OR NEW.claim_token IS NOT NULL OR NEW.claim_expires_at IS NOT NULL
           OR NEW.sealed_at IS NOT NULL OR NEW.completed_at IS NOT NULL OR NEW.final_summary_jsonb IS NOT NULL
           OR NEW.last_modified_at IS DISTINCT FROM NEW.created_at THEN
            RAISE EXCEPTION 'legacy adoption arc must start as an empty, unsealed Evidence population at revision one (arc=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.storage_profile_id IS DISTINCT FROM OLD.storage_profile_id
       OR NEW.storage_profile_revision_id IS DISTINCT FROM OLD.storage_profile_revision_id
       OR NEW.profile_revision IS DISTINCT FROM OLD.profile_revision
       OR NEW.created_by IS DISTINCT FROM OLD.created_by OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'legacy adoption arc stable identity is immutable (arc=%).', OLD.id;
    END IF;
    IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
        RAISE EXCEPTION 'legacy adoption arc revision must advance exactly once and time must not rewind (arc=%, old_revision=%, new_revision=%).',
            OLD.id, OLD.revision, NEW.revision;
    END IF;
    IF OLD.state IN ('Completed', 'Expired', 'Stale') THEN
        RAISE EXCEPTION 'legacy adoption terminal tombstone is immutable (arc=%, state=%).', OLD.id, OLD.state;
    END IF;

    IF OLD.sealed_at IS NULL THEN
        IF NEW.sealed_at IS NULL THEN
            RAISE EXCEPTION 'the first legacy adoption arc revision must seal its closed population (arc=%).', OLD.id;
        END IF;
        SELECT COUNT(*) INTO materialized_members
        FROM legacy_placement_adoption_member member WHERE member.arc_id = OLD.id;
        IF NEW.member_count <> materialized_members THEN
            RAISE EXCEPTION 'legacy adoption seal must record its exact materialized member count (arc=%, recorded=%, actual=%).',
                OLD.id, NEW.member_count, materialized_members;
        END IF;
    ELSIF NEW.sealed_at IS DISTINCT FROM OLD.sealed_at OR NEW.member_count IS DISTINCT FROM OLD.member_count THEN
        RAISE EXCEPTION 'legacy adoption seal and original member count are immutable (arc=%).', OLD.id;
    END IF;

    IF OLD.witness_workflow_artifact_id IS NOT NULL
       AND NEW.witness_workflow_artifact_id IS DISTINCT FROM OLD.witness_workflow_artifact_id THEN
        IF OLD.phase <> 'Evidence' OR NEW.phase NOT IN ('Evidence', 'Minting') OR NEW.witness_workflow_artifact_id IS NULL
           OR NOT EXISTS (
               SELECT 1
               FROM legacy_placement_adoption_member prior
               JOIN legacy_placement_adoption_member candidate ON candidate.arc_id = prior.arc_id
               WHERE prior.arc_id = OLD.id
                 AND prior.workflow_artifact_id = OLD.witness_workflow_artifact_id
                 AND candidate.workflow_artifact_id = NEW.witness_workflow_artifact_id
                 AND (candidate.size_bytes, candidate.position) < (prior.size_bytes, prior.position)
           ) THEN
            RAISE EXCEPTION 'legacy adoption witness may only move to a smaller confirmed Evidence member (arc=%, old=%, new=%).',
                OLD.id, OLD.witness_workflow_artifact_id, NEW.witness_workflow_artifact_id;
        END IF;
    END IF;
    IF OLD.terminal_state IS NOT NULL AND NEW.terminal_state IS DISTINCT FROM OLD.terminal_state THEN
        RAISE EXCEPTION 'legacy adoption terminal intent is immutable once selected (arc=%).', OLD.id;
    END IF;
    IF OLD.final_summary_jsonb IS NOT NULL AND NEW.final_summary_jsonb IS DISTINCT FROM OLD.final_summary_jsonb THEN
        RAISE EXCEPTION 'legacy adoption final summary is immutable once recorded (arc=%).', OLD.id;
    END IF;

    IF OLD.state = 'Active' THEN
        IF NEW.state = 'Active' THEN
            IF NOT ((OLD.phase = 'Evidence' AND NEW.phase IN ('Evidence', 'Minting'))
                    OR (OLD.phase = 'Minting' AND NEW.phase = 'Minting')) THEN
                RAISE EXCEPTION 'legacy adoption illegal active phase transition (arc=%, old=%, new=%).', OLD.id, OLD.phase, NEW.phase;
            END IF;
        ELSIF NEW.state = 'Cleaning' THEN
            IF NEW.phase <> 'Cleaning' THEN
                RAISE EXCEPTION 'legacy adoption Cleaning state requires its Cleaning phase (arc=%).', OLD.id;
            END IF;
        ELSIF NOT (OLD.phase = 'Minting' AND NEW.phase = 'Minting' AND NEW.state = 'Completed') THEN
            RAISE EXCEPTION 'legacy adoption illegal state transition (arc=%, old=%, new=%).', OLD.id, OLD.state, NEW.state;
        END IF;
    ELSIF OLD.state = 'Cleaning' THEN
        IF NEW.phase <> 'Cleaning' OR NEW.state NOT IN ('Cleaning', OLD.terminal_state)
           OR NEW.terminal_state IS DISTINCT FROM OLD.terminal_state THEN
            RAISE EXCEPTION 'legacy adoption Cleaning may only continue or reach its declared terminal state (arc=%, terminal=%, attempted=%).',
                OLD.id, OLD.terminal_state, NEW.state;
        END IF;
    END IF;

    IF OLD.phase = NEW.phase AND NEW.current_position < OLD.current_position THEN
        RAISE EXCEPTION 'legacy adoption page position is monotonic inside one phase (arc=%, old=%, new=%).',
            OLD.id, OLD.current_position, NEW.current_position;
    END IF;
    IF OLD.phase = 'Evidence' AND NEW.phase = 'Minting' AND NEW.current_position <> 0 THEN
        RAISE EXCEPTION 'legacy adoption Minting must restart at the beginning of the sealed population (arc=%, position=%).',
            OLD.id, NEW.current_position;
    END IF;
    IF NEW.phase = 'Cleaning' AND NEW.current_position IS DISTINCT FROM OLD.current_position THEN
        RAISE EXCEPTION 'legacy adoption Cleaning cannot rewrite the last durable page position (arc=%).', OLD.id;
    END IF;

    IF NEW.claim_token IS NOT NULL AND NEW.claim_expires_at <= clock_timestamp() THEN
        RAISE EXCEPTION 'legacy adoption claim must have a future bounded expiry (arc=%).', OLD.id;
    END IF;
    IF NEW.claim_token IS DISTINCT FROM OLD.claim_token AND NEW.claim_token IS NOT NULL
       AND OLD.claim_expires_at > clock_timestamp() THEN
        RAISE EXCEPTION 'legacy adoption live claim cannot be replaced (arc=%, token=%).', OLD.id, OLD.claim_token;
    END IF;
    IF NEW.claim_token IS NOT NULL
       AND (NEW.phase IS DISTINCT FROM OLD.phase OR NEW.state IS DISTINCT FROM OLD.state
            OR NEW.terminal_state IS DISTINCT FROM OLD.terminal_state
            OR NEW.witness_workflow_artifact_id IS DISTINCT FROM OLD.witness_workflow_artifact_id
            OR NEW.current_position IS DISTINCT FROM OLD.current_position
            OR NEW.member_count IS DISTINCT FROM OLD.member_count OR NEW.sealed_at IS DISTINCT FROM OLD.sealed_at
            OR NEW.completed_at IS DISTINCT FROM OLD.completed_at
            OR NEW.final_summary_jsonb IS DISTINCT FROM OLD.final_summary_jsonb) THEN
        RAISE EXCEPTION 'legacy adoption claim acquisition or renewal cannot mutate lifecycle state (arc=%).', OLD.id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER legacy_placement_adoption_arc_enforce_lifecycle
    BEFORE INSERT OR UPDATE OR DELETE ON legacy_placement_adoption_arc
    FOR EACH ROW EXECUTE FUNCTION legacy_placement_adoption_arc_guard();

CREATE OR REPLACE FUNCTION legacy_placement_adoption_member_reject_update() RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'legacy adoption membership is immutable (arc=%, position=%).', OLD.arc_id, OLD.position;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER legacy_placement_adoption_member_reject_update
    BEFORE UPDATE ON legacy_placement_adoption_member
    FOR EACH ROW EXECUTE FUNCTION legacy_placement_adoption_member_reject_update();

CREATE OR REPLACE FUNCTION legacy_placement_adoption_member_guard_insert() RETURNS TRIGGER AS $$
DECLARE
    invalid_arc UUID;
BEGIN
    SELECT inserted.arc_id INTO invalid_arc
    FROM (SELECT DISTINCT arc_id FROM inserted_members) inserted
    LEFT JOIN legacy_placement_adoption_arc arc ON arc.id = inserted.arc_id
    WHERE arc.id IS NULL OR arc.state IS DISTINCT FROM 'Active'
       OR arc.phase IS DISTINCT FROM 'Evidence' OR arc.sealed_at IS NOT NULL
    LIMIT 1;

    IF FOUND THEN
        RAISE EXCEPTION 'legacy adoption membership accepts rows only while an Evidence arc is being sealed (arc=%).', invalid_arc;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER legacy_placement_adoption_member_guard_insert
    AFTER INSERT ON legacy_placement_adoption_member
    REFERENCING NEW TABLE AS inserted_members
    FOR EACH STATEMENT EXECUTE FUNCTION legacy_placement_adoption_member_guard_insert();

CREATE OR REPLACE FUNCTION legacy_placement_adoption_member_guard_delete() RETURNS TRIGGER AS $$
DECLARE
    invalid_arc UUID;
    invalid_position BIGINT;
    arc_phase VARCHAR(16);
    arc_position BIGINT;
    arc_witness UUID;
BEGIN
    SELECT deleted.arc_id, deleted.position, arc.phase, arc.current_position, arc.witness_workflow_artifact_id
    INTO invalid_arc, invalid_position, arc_phase, arc_position, arc_witness
    FROM deleted_members deleted
    JOIN legacy_placement_adoption_arc arc ON arc.id = deleted.arc_id
    WHERE arc.state = 'Active' AND (
        arc.phase = 'Evidence'
        OR arc.phase = 'Minting' AND (
            arc.sealed_at IS NULL OR deleted.position > arc.current_position
            OR deleted.workflow_artifact_id = arc.witness_workflow_artifact_id))
    LIMIT 1;

    IF FOUND THEN
        RAISE EXCEPTION 'legacy adoption % may delete only an advanced non-witness Minting member or a Cleaning member (arc=%, position=%, head=%, witness=%).',
            arc_phase, invalid_arc, invalid_position, arc_position, arc_witness;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER legacy_placement_adoption_member_guard_delete
    AFTER DELETE ON legacy_placement_adoption_member
    REFERENCING OLD TABLE AS deleted_members
    FOR EACH STATEMENT EXECUTE FUNCTION legacy_placement_adoption_member_guard_delete();

-- The constraint trigger deliberately re-reads the FINAL row instead of validating its queued NEW image. Arc creation
-- inserts an unsealed revision-one row, copies its closed membership, then seals it in the same transaction. Every
-- queued arc event therefore proves the committed shape while still permitting that construction sequence. Member
-- insertion has no per-row deferred trigger: seal checks the exact count once, avoiding O(N^2) commit work.
CREATE OR REPLACE FUNCTION legacy_placement_adoption_require_committed_shape() RETURNS TRIGGER AS $$
DECLARE
    target_arc_id UUID;
    arc legacy_placement_adoption_arc%ROWTYPE;
    remaining_members BIGINT;
BEGIN
    target_arc_id := NEW.id;

    SELECT * INTO arc FROM legacy_placement_adoption_arc WHERE id = target_arc_id;
    IF NOT FOUND THEN RETURN NULL; END IF;

    IF arc.sealed_at IS NULL THEN
        RAISE EXCEPTION 'committed legacy adoption arc must have a sealed population (arc=%).', target_arc_id;
    END IF;
    IF arc.state = 'Active' AND arc.phase = 'Minting' AND NOT EXISTS (
        SELECT 1 FROM legacy_placement_adoption_member member
        WHERE member.arc_id = target_arc_id
          AND member.workflow_artifact_id = arc.witness_workflow_artifact_id
    ) THEN
        RAISE EXCEPTION 'live legacy adoption Minting must retain the exact evidence witness until terminal commit (arc=%, witness=%).',
            target_arc_id, arc.witness_workflow_artifact_id;
    END IF;
    IF arc.state = 'Cleaning'
       AND (arc.phase <> 'Cleaning' OR arc.terminal_state IS NULL OR arc.final_summary_jsonb IS NULL) THEN
        RAISE EXCEPTION 'legacy adoption Cleaning requires a terminal intent and durable final summary (arc=%).', target_arc_id;
    END IF;
    IF arc.state IN ('Completed', 'Expired', 'Stale') THEN
        SELECT COUNT(*) INTO remaining_members
        FROM legacy_placement_adoption_member member WHERE member.arc_id = target_arc_id;
        IF remaining_members <> 0 OR arc.claim_token IS NOT NULL OR arc.claim_expires_at IS NOT NULL
           OR arc.completed_at IS NULL OR arc.final_summary_jsonb IS NULL OR arc.terminal_state IS DISTINCT FROM arc.state THEN
            RAISE EXCEPTION 'legacy adoption terminal tombstone requires zero members, no claim, and a complete final summary (arc=%, members=%).',
                target_arc_id, remaining_members;
        END IF;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER legacy_placement_adoption_arc_require_committed_shape
    AFTER INSERT OR UPDATE ON legacy_placement_adoption_arc
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION legacy_placement_adoption_require_committed_shape();

COMMENT ON TABLE legacy_placement_adoption_arc IS
    'Lease-fenced control-plane state and terminal tombstone for one closed, team-wide legacy placement adoption population. Never a runtime artifact link.';
COMMENT ON TABLE legacy_placement_adoption_member IS
    'Closed source-identity snapshot for one adoption arc. No workflow_artifact FK by design: retention may delete the source before exact commit revalidation.';
