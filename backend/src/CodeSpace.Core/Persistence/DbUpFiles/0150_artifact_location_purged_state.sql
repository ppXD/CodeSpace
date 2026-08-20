-- 0150_artifact_location_purged_state.sql
--
-- Adds the artifact_location state a purge can leave behind and still have the same content written again: Purged
-- means the bytes were intentionally removed and this row is the record of that removal.
--
-- It exists because the states 0127 shipped make purged content permanently unstorable under its profile revision.
-- ux_artifact_location_profile_object_key forbids a second row for the same (team, profile revision, object key), so
-- a fresh row is impossible; artifact_cas_location_guard refuses every UPDATE whose OLD.state is 'Deleted', so the
-- existing row cannot be revived; and the CAS commit only ever re-verifies an existing row. A purge that leaves
-- content unwritable is data loss, not reclamation.
--
-- Purged is reachable ONLY from the 'Deleting' claim, and no location may be created in it. A writer therefore reads
-- (state, revision) before it uploads and re-checks it at commit: any purge that advances the row invalidates that
-- fence. The obligation that pairs with it belongs to the purge and cannot be checked here, because byte removal
-- happens outside the database — a purge MUST claim 'Deleting' before it removes a byte. The trigger enforces the
-- half it can see: 'Purged' is admitted from no other state.
--
-- 'Deleted' keeps its existing meaning and its terminal trigger. Nothing in this migration writes either state; the
-- writer that produces them is the routed purge, which is not in this script.
--
-- Rollback: restore both state CHECK constraints and the 0127 body of artifact_cas_location_guard(), after confirming
-- no artifact_location or artifact_location_event row is in state 'Purged'.

ALTER TABLE artifact_location
    DROP CONSTRAINT ck_artifact_location_state,
    ADD CONSTRAINT ck_artifact_location_state CHECK (
        state IN ('Pending', 'Available', 'Missing', 'Corrupt', 'Deleting', 'Deleted', 'Failed', 'Purged'));

ALTER TABLE artifact_location_event
    DROP CONSTRAINT ck_artifact_location_event_state,
    ADD CONSTRAINT ck_artifact_location_event_state CHECK (
        state IN ('Pending', 'Available', 'Missing', 'Corrupt', 'Deleting', 'Deleted', 'Failed', 'Purged'));

CREATE OR REPLACE FUNCTION artifact_cas_location_guard() RETURNS trigger AS $$
DECLARE
    expected_size BIGINT;
    expected_digest BYTEA;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'artifact_location is durable identity — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.revision <> 1 THEN
            RAISE EXCEPTION 'artifact_location first revision must be 1 (id=%, revision=%).', NEW.id, NEW.revision;
        END IF;
        IF NEW.state = 'Purged' THEN
            RAISE EXCEPTION 'artifact_location cannot be created Purged (id=%).', NEW.id;
        END IF;
    ELSE
        IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
           OR NEW.artifact_object_id IS DISTINCT FROM OLD.artifact_object_id
           OR NEW.storage_profile_revision_id IS DISTINCT FROM OLD.storage_profile_revision_id
           OR NEW.locator IS DISTINCT FROM OLD.locator OR NEW.object_key IS DISTINCT FROM OLD.object_key
           OR NEW.created_date IS DISTINCT FROM OLD.created_date OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
            RAISE EXCEPTION 'artifact_location stable identity is immutable (id=%).', OLD.id;
        END IF;
        IF NEW.revision <> OLD.revision + 1 THEN
            RAISE EXCEPTION 'artifact_location revision must advance exactly once (id=%, old=%, new=%).', OLD.id, OLD.revision, NEW.revision;
        END IF;
        IF OLD.state = 'Deleted' THEN
            RAISE EXCEPTION 'artifact_location Deleted state is terminal (id=%).', OLD.id;
        END IF;
        IF NEW.state = 'Purged' AND OLD.state <> 'Deleting' THEN
            RAISE EXCEPTION 'artifact_location Purged is only reachable from the Deleting claim (id=%, old=%).', OLD.id, OLD.state;
        END IF;
    END IF;

    IF NEW.state = 'Available' THEN
        SELECT size_bytes, digest INTO expected_size, expected_digest
        FROM artifact_object WHERE team_id = NEW.team_id AND id = NEW.artifact_object_id;
        IF NEW.observed_size_bytes IS DISTINCT FROM expected_size THEN
            RAISE EXCEPTION 'artifact_location Available size does not match artifact_object (id=%).', NEW.id;
        END IF;
        IF NEW.provider_checksum_algorithm IS DISTINCT FROM 'Sha256'
           OR NEW.provider_checksum IS DISTINCT FROM expected_digest THEN
            RAISE EXCEPTION 'artifact_location Available requires exact Sha256 matching artifact_object (id=%).', NEW.id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
