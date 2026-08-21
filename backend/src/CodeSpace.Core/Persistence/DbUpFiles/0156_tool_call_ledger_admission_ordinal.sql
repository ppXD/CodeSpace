-- 0156_tool_call_ledger_admission_ordinal.sql
--
-- A future observation-only workflow_run_tool_call projector needs a durable order for governed calls. CreatedDate
-- is an application/audit timestamp and Id is random; neither is a source ordinal. A plain sequence is also wrong:
-- transaction A can allocate first but commit after transaction B, making an already-projected B precede a late,
-- lower A. This migration therefore allocates a ONE-BASED ordinal under a transaction-scoped advisory lock derived
-- from AgentRunId. The lock is held until commit/rollback: same-run allocation and commit are serialized, while
-- different AgentRuns remain independent (a hash collision can only over-serialize, never corrupt ordering).
--
-- Existing rows stay NULL. There is no truthful way to reconstruct their source order from a heap position,
-- timestamp, or UUID, so legacy rows are explicitly ineligible for a later ordered projection. The partial unique
-- btree both enforces one rank per AgentRun and supplies the allocator's MAX lookup without scanning run history.
-- This is source admission only: ToolCallLedger remains the sole approval, execution, idempotency, replay, and
-- terminal authority. Every ledger row, including decision.request, receives this SOURCE admission rank. A later
-- projector that excludes decision rows must preserve possible gaps and must not relabel it as a dense side-effect
-- call ordinal. No workflow_run_tool_call rows are written here.

ALTER TABLE tool_call_ledger
    ADD COLUMN IF NOT EXISTS admission_ordinal BIGINT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_tool_call_ledger_admission_ordinal_positive'
          AND conrelid = 'tool_call_ledger'::regclass
    ) THEN
        ALTER TABLE tool_call_ledger
            ADD CONSTRAINT ck_tool_call_ledger_admission_ordinal_positive
            CHECK (admission_ordinal IS NULL OR admission_ordinal > 0);
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_tool_call_ledger_run_admission_ordinal
    ON tool_call_ledger (agent_run_id, admission_ordinal)
    WHERE admission_ordinal IS NOT NULL;

CREATE OR REPLACE FUNCTION tool_call_ledger_assign_admission_ordinal()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $$
DECLARE
    parent_team_id UUID;
BEGIN
    -- hashtextextended is deterministic for the same UUID on this server. Collisions merely make two unrelated runs
    -- wait; the ordinal lookup and unique index remain keyed by the full UUID, so a collision cannot mix identities.
    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.agent_run_id::text, 156));

    -- AgentRun is intentionally a soft link because the ledger outlives the execution row. When the parent still
    -- exists, however, its tenant is authoritative. When it does not, prior ledger history still pins one team for
    -- this AgentRun identity. Both checks run under the same admission lock.
    SELECT team_id INTO parent_team_id
    FROM public.agent_run
    WHERE id = NEW.agent_run_id;

    IF FOUND AND parent_team_id <> NEW.team_id THEN
        RAISE EXCEPTION 'tool_call_ledger AgentRun % belongs to team %, not %', NEW.agent_run_id, parent_team_id, NEW.team_id
            USING ERRCODE = '23514';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM public.tool_call_ledger
        WHERE agent_run_id = NEW.agent_run_id
          AND team_id <> NEW.team_id
    ) THEN
        RAISE EXCEPTION 'tool_call_ledger AgentRun % already has a different team identity', NEW.agent_run_id
            USING ERRCODE = '23514';
    END IF;

    SELECT COALESCE(MAX(admission_ordinal), 0) + 1 INTO NEW.admission_ordinal
    FROM public.tool_call_ledger
    WHERE agent_run_id = NEW.agent_run_id
      AND admission_ordinal IS NOT NULL;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_tool_call_ledger_assign_admission_ordinal ON tool_call_ledger;
CREATE TRIGGER trg_tool_call_ledger_assign_admission_ordinal
    BEFORE INSERT ON tool_call_ledger
    FOR EACH ROW
    EXECUTE FUNCTION tool_call_ledger_assign_admission_ordinal();

CREATE OR REPLACE FUNCTION tool_call_ledger_protect_admission_identity()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $$
BEGIN
    IF OLD.agent_run_id IS DISTINCT FROM NEW.agent_run_id
       OR OLD.team_id IS DISTINCT FROM NEW.team_id
       OR OLD.admission_ordinal IS DISTINCT FROM NEW.admission_ordinal THEN
        RAISE EXCEPTION 'tool_call_ledger admission identity (team_id, agent_run_id, admission_ordinal) is immutable'
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_tool_call_ledger_protect_admission_identity ON tool_call_ledger;
CREATE TRIGGER trg_tool_call_ledger_protect_admission_identity
    BEFORE UPDATE OF team_id, agent_run_id, admission_ordinal ON tool_call_ledger
    FOR EACH ROW
    EXECUTE FUNCTION tool_call_ledger_protect_admission_identity();

COMMENT ON COLUMN tool_call_ledger.admission_ordinal IS
    'Database-owned one-based admission order within AgentRunId, allocated under a per-AgentRun transaction lock and immutable after insert. NULL is honest legacy history and is not eligible for ordered projection.';

COMMENT ON INDEX ux_tool_call_ledger_run_admission_ordinal IS
    'One durable source rank per AgentRun; partial so legacy NULL rows remain representable. Also supports the allocator MAX lookup.';
