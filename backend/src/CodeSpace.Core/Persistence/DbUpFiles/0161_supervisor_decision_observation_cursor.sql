-- 0161_supervisor_decision_observation_cursor.sql
--
-- supervisor_decision.sequence is the original replay order. It is a BIGSERIAL allocation token: allocation happens
-- before commit, and the mutable status/outcome/error path keeps changing after that one token was assigned. It is
-- therefore neither a safe live observation cursor nor a change revision. Keep it untouched for execution/rehydrate
-- compatibility and add two observation-only database-owned axes:
--
--   story_order          immutable ordering of decision identities within a run;
--   observation_revision the latest admitted version of a row's observable state.
--
-- Both values are gapful global sequence values, but every allocation is made only AFTER acquiring a transaction-scoped
-- advisory lock for the exact SupervisorRunId. The lock is held through commit/rollback. Mutations of one run are thus
-- commit-admitted in revision order while unrelated runs remain independently writable. A rollback consumes values but
-- publishes no row/revision; gaps are honest and must never be interpreted as missing records.
--
-- Historical rows are backfilled once from their immutable existing Sequence. That preserves deterministic legacy
-- allocation order; it never reconstructed commit order, which the old BIGSERIAL did not record. No API consumes these
-- columns in this migration. This is additive observation substrate only: decision execution, replay, idempotency,
-- terminal authority and the original Sequence are unchanged.

ALTER TABLE supervisor_decision
    ADD COLUMN IF NOT EXISTS story_order BIGINT NULL,
    ADD COLUMN IF NOT EXISTS observation_revision BIGINT NULL;

CREATE SEQUENCE IF NOT EXISTS supervisor_decision_story_order_seq AS BIGINT;
CREATE SEQUENCE IF NOT EXISTS supervisor_decision_observation_revision_seq AS BIGINT;

-- One-time deterministic legacy snapshot. Existing Sequence is globally unique, so it is also unique within each run.
-- This labels legacy allocation order only; it deliberately makes no claim about the transactions' historical commits.
UPDATE supervisor_decision
SET story_order = sequence,
    observation_revision = sequence
WHERE story_order IS NULL OR observation_revision IS NULL;

DO $$
DECLARE
    max_story BIGINT;
    max_revision BIGINT;
BEGIN
    SELECT MAX(story_order), MAX(observation_revision)
    INTO max_story, max_revision
    FROM supervisor_decision;

    IF max_story IS NULL THEN
        PERFORM setval('supervisor_decision_story_order_seq'::regclass, 1, false);
    ELSE
        PERFORM setval('supervisor_decision_story_order_seq'::regclass, max_story, true);
    END IF;

    IF max_revision IS NULL THEN
        PERFORM setval('supervisor_decision_observation_revision_seq'::regclass, 1, false);
    ELSE
        PERFORM setval('supervisor_decision_observation_revision_seq'::regclass, max_revision, true);
    END IF;
END;
$$;

ALTER SEQUENCE supervisor_decision_story_order_seq OWNED BY supervisor_decision.story_order;
ALTER SEQUENCE supervisor_decision_observation_revision_seq OWNED BY supervisor_decision.observation_revision;

ALTER TABLE supervisor_decision
    ALTER COLUMN story_order SET NOT NULL,
    ALTER COLUMN observation_revision SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_supervisor_decision_story_order_positive'
          AND conrelid = 'supervisor_decision'::regclass
    ) THEN
        ALTER TABLE supervisor_decision
            ADD CONSTRAINT ck_supervisor_decision_story_order_positive CHECK (story_order > 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_supervisor_decision_observation_revision_positive'
          AND conrelid = 'supervisor_decision'::regclass
    ) THEN
        ALTER TABLE supervisor_decision
            ADD CONSTRAINT ck_supervisor_decision_observation_revision_positive CHECK (observation_revision > 0);
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_supervisor_decision_run_story_order
    ON supervisor_decision (supervisor_run_id, story_order);

CREATE UNIQUE INDEX IF NOT EXISTS ux_supervisor_decision_run_observation_revision
    ON supervisor_decision (supervisor_run_id, observation_revision);

CREATE OR REPLACE FUNCTION supervisor_decision_assign_observation_cursor()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $$
DECLARE
    lock_run_id UUID;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NEW.team_id IS DISTINCT FROM OLD.team_id
           OR NEW.supervisor_run_id IS DISTINCT FROM OLD.supervisor_run_id THEN
            RAISE EXCEPTION 'supervisor_decision observation scope (team_id, supervisor_run_id) is immutable (decision=%)', OLD.id
                USING ERRCODE = '23514';
        END IF;

        IF NEW.story_order IS DISTINCT FROM OLD.story_order THEN
            RAISE EXCEPTION 'supervisor_decision story_order is immutable (decision=%, old=%, new=%)', OLD.id, OLD.story_order, NEW.story_order
                USING ERRCODE = '23514';
        END IF;

        lock_run_id := OLD.supervisor_run_id;
    ELSE
        lock_run_id := NEW.supervisor_run_id;
    END IF;

    -- Same-run INSERTs and observable UPDATEs cannot become visible out of cursor order. Hash collisions only
    -- over-serialize unrelated runs; full UUID predicates and indexes retain identity correctness.
    PERFORM pg_advisory_xact_lock(hashtextextended(lock_run_id::text, 161));

    IF TG_OP = 'INSERT' THEN
        -- Always replace caller/EF values. There is no column default: allocation cannot happen before the run lock.
        NEW.story_order := nextval('supervisor_decision_story_order_seq'::regclass);
    END IF;

    -- Every accepted UPDATE advances, including status/outcome/error/timestamp enrichment. Payload is already frozen by
    -- 0053's journal guard; an attempted illegal mutation is rejected before this alphabetically-later trigger runs.
    NEW.observation_revision := nextval('supervisor_decision_observation_revision_seq'::regclass);

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_supervisor_decision_assign_observation_cursor ON supervisor_decision;
CREATE TRIGGER trg_supervisor_decision_assign_observation_cursor
    BEFORE INSERT OR UPDATE ON supervisor_decision
    FOR EACH ROW EXECUTE FUNCTION supervisor_decision_assign_observation_cursor();

COMMENT ON COLUMN supervisor_decision.story_order IS
    'Immutable gapful story order. New rows are allocated after a per-SupervisorRun transaction lock; legacy rows preserve existing BIGSERIAL allocation order and never claim reconstructed commit order.';

COMMENT ON COLUMN supervisor_decision.observation_revision IS
    'Database-owned gapful observation watermark. Advances on every INSERT/UPDATE after a per-SupervisorRun transaction lock; scoped cursors must always include SupervisorRunId.';

COMMENT ON INDEX ux_supervisor_decision_run_story_order IS
    'Supports exact-run chronological keyset pages without reading payload/outcome bodies.';

COMMENT ON INDEX ux_supervisor_decision_run_observation_revision IS
    'Supports exact-run observation-change keyset reads without reading payload/outcome bodies.';
