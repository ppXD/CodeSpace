-- 0188_legacy_placement_adoption_budgets_and_audit.sql
--
-- Adds an audit-versioned, monotonic whole-arc ledger beside 0186. Existing arcs remain version 0 and replay their
-- original compact summary; new arcs are version 1. Every completed/aborted claimed pass writes at most one
-- secret-free row keyed by (arc, claim token), in the same transaction that applies its counter delta. Claim renewal
-- and bounded unclaimed Cleaning deliberately write no audit row. No object key, locator, provider message, exception text, or credential appears
-- here. Terminal tombstone deletion after the existing 30-day retention cascades the bounded per-arc audit.
--
-- Deployment cost: ADD COLUMN takes a brief metadata lock and the new table/index are empty. PostgreSQL 11+ stores
-- constant defaults without rewriting the arc table. The UPDATE only touches a presently claimed arc, if one exists.
-- DbUp runs this file transactionally. Rollback requires dropping the audit table/guards and the added columns.

ALTER TABLE legacy_placement_adoption_arc
    ADD COLUMN audit_version              SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN claim_started_at           TIMESTAMPTZ NULL,
    ADD COLUMN evidence_examined          BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN evidence_resolved          BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN evidence_confirmed         BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN mint_examined              BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN available                  BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN missing                    BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN corrupt                    BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN already_recorded           BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN conflicts                  BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN retryable                  BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN read_bytes                 BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN completed_passes           BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN budget_yields              BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN oversized_passes           BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN last_settled_claim_token   UUID NULL;

UPDATE legacy_placement_adoption_arc
SET claim_started_at = last_modified_at
WHERE claim_token IS NOT NULL;

ALTER TABLE legacy_placement_adoption_arc
    ADD CONSTRAINT ck_legacy_adoption_arc_audit_version CHECK (audit_version IN (0, 1)),
    ADD CONSTRAINT ck_legacy_adoption_arc_claim_started CHECK (
        (claim_token IS NULL) = (claim_started_at IS NULL)),
    ADD CONSTRAINT ck_legacy_adoption_arc_audit_bounds CHECK (
        evidence_examined >= 0 AND evidence_resolved >= 0 AND evidence_confirmed >= 0
        AND evidence_confirmed <= evidence_resolved AND evidence_resolved <= evidence_examined
        AND evidence_examined <= member_count
        AND mint_examined >= 0 AND mint_examined <= member_count
        AND available >= 0 AND missing >= 0 AND corrupt >= 0 AND already_recorded >= 0 AND conflicts >= 0
        AND available + missing + corrupt + already_recorded + conflicts = mint_examined
        AND retryable >= 0 AND read_bytes >= 0 AND completed_passes >= 0
        AND budget_yields >= 0 AND budget_yields <= completed_passes
        AND oversized_passes >= 0 AND oversized_passes <= completed_passes
        AND (completed_passes = 0) = (last_settled_claim_token IS NULL));

CREATE TABLE legacy_placement_adoption_pass_audit (
    arc_id                    UUID        NOT NULL REFERENCES legacy_placement_adoption_arc(id) ON DELETE CASCADE,
    claim_token               UUID        NOT NULL,
    phase                     VARCHAR(16) NOT NULL,
    outcome                   VARCHAR(16) NOT NULL,
    yield_reason              VARCHAR(24) NOT NULL,
    failure_code              VARCHAR(32) NOT NULL,
    start_position            BIGINT      NOT NULL,
    end_position              BIGINT      NOT NULL,
    examined                  BIGINT      NOT NULL,
    resolved                  BIGINT      NOT NULL,
    confirmed                 BIGINT      NOT NULL,
    evidence_examined_delta   BIGINT      NOT NULL,
    evidence_resolved_delta   BIGINT      NOT NULL,
    evidence_confirmed_delta  BIGINT      NOT NULL,
    mint_examined_delta       BIGINT      NOT NULL,
    available_delta           BIGINT      NOT NULL,
    missing_delta             BIGINT      NOT NULL,
    corrupt_delta             BIGINT      NOT NULL,
    already_recorded_delta    BIGINT      NOT NULL,
    conflicts_delta           BIGINT      NOT NULL,
    retryable_delta           BIGINT      NOT NULL,
    read_bytes_delta          BIGINT      NOT NULL,
    oversized_item            BOOLEAN     NOT NULL,
    started_at                TIMESTAMPTZ NOT NULL,
    completed_at              TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (arc_id, claim_token),
    CONSTRAINT ck_legacy_adoption_pass_vocab CHECK (
        phase IN ('Evidence', 'Minting')
        AND outcome IN ('Advanced', 'Retryable', 'Aborted', 'Interrupted')
        AND yield_reason IN ('None', 'RowLimit', 'ByteBudget', 'TimeBudget', 'ProviderRetryable')
        AND failure_code IN ('None', 'ProviderTransient', 'ProviderRejected', 'ProgrammingFault', 'CallerCancelled',
            'CursorStale', 'AdmissionEvidenceMissing')),
    CONSTRAINT ck_legacy_adoption_pass_bounds CHECK (
        start_position >= 0 AND end_position >= start_position
        AND examined >= 0 AND resolved >= 0 AND confirmed >= 0 AND confirmed <= resolved AND resolved <= examined
        AND evidence_examined_delta >= 0 AND evidence_resolved_delta >= 0 AND evidence_confirmed_delta >= 0
        AND mint_examined_delta >= 0 AND available_delta >= 0 AND missing_delta >= 0 AND corrupt_delta >= 0
        AND already_recorded_delta >= 0 AND conflicts_delta >= 0 AND retryable_delta >= 0 AND read_bytes_delta >= 0
        AND completed_at >= started_at)
);

CREATE OR REPLACE FUNCTION legacy_placement_adoption_pass_audit_guard() RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'legacy adoption pass audit is append-only (arc=%, claim=%).', OLD.arc_id, OLD.claim_token;
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF pg_trigger_depth() <= 1 THEN
            RAISE EXCEPTION 'legacy adoption pass audit may leave only through its parent tombstone cascade (arc=%, claim=%).',
                OLD.arc_id, OLD.claim_token;
        END IF;
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER legacy_placement_adoption_pass_audit_enforce_append_only
    BEFORE UPDATE OR DELETE ON legacy_placement_adoption_pass_audit
    FOR EACH ROW EXECUTE FUNCTION legacy_placement_adoption_pass_audit_guard();

CREATE OR REPLACE FUNCTION legacy_placement_adoption_arc_audit_guard() RETURNS TRIGGER AS $$
BEGIN
    IF NEW.audit_version IS DISTINCT FROM OLD.audit_version THEN
        RAISE EXCEPTION 'legacy adoption audit version is immutable (arc=%).', OLD.id;
    END IF;
    IF NEW.audit_version = 0 THEN RETURN NEW; END IF;

    IF NEW.evidence_examined < OLD.evidence_examined OR NEW.evidence_resolved < OLD.evidence_resolved
       OR NEW.evidence_confirmed < OLD.evidence_confirmed OR NEW.mint_examined < OLD.mint_examined
       OR NEW.available < OLD.available OR NEW.missing < OLD.missing OR NEW.corrupt < OLD.corrupt
       OR NEW.already_recorded < OLD.already_recorded OR NEW.conflicts < OLD.conflicts
       OR NEW.retryable < OLD.retryable OR NEW.read_bytes < OLD.read_bytes
       OR NEW.completed_passes < OLD.completed_passes OR NEW.budget_yields < OLD.budget_yields
       OR NEW.oversized_passes < OLD.oversized_passes THEN
        RAISE EXCEPTION 'legacy adoption cumulative audit cannot rewind (arc=%).', OLD.id;
    END IF;
    IF NEW.completed_passes > OLD.completed_passes + 1 THEN
        RAISE EXCEPTION 'one legacy adoption revision may settle at most one claimed pass (arc=%).', OLD.id;
    END IF;
    IF NEW.completed_passes = OLD.completed_passes + 1 THEN
        IF OLD.claim_token IS NULL OR NEW.last_settled_claim_token IS DISTINCT FROM OLD.claim_token THEN
            RAISE EXCEPTION 'legacy adoption settled audit must name the claim held by the arc (arc=%, held=%, settled=%).',
                OLD.id, OLD.claim_token, NEW.last_settled_claim_token;
        END IF;
    ELSIF NEW.last_settled_claim_token IS DISTINCT FROM OLD.last_settled_claim_token THEN
        RAISE EXCEPTION 'legacy adoption last settled claim changes only with one settled pass (arc=%).', OLD.id;
    END IF;

    IF NEW.claim_token IS NOT DISTINCT FROM OLD.claim_token AND NEW.claim_token IS NOT NULL
       AND NEW.claim_started_at IS DISTINCT FROM OLD.claim_started_at THEN
        RAISE EXCEPTION 'legacy adoption claim renewal cannot rewrite claim start time (arc=%, claim=%).', OLD.id, OLD.claim_token;
    END IF;
    IF NEW.claim_token IS DISTINCT FROM OLD.claim_token AND NEW.claim_token IS NOT NULL
       AND NEW.claim_started_at IS NULL THEN
        RAISE EXCEPTION 'legacy adoption claim acquisition requires a start time (arc=%, claim=%).', OLD.id, NEW.claim_token;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER legacy_placement_adoption_arc_enforce_audit
    BEFORE UPDATE ON legacy_placement_adoption_arc
    FOR EACH ROW EXECUTE FUNCTION legacy_placement_adoption_arc_audit_guard();

CREATE OR REPLACE FUNCTION legacy_placement_adoption_require_audit_shape() RETURNS TRIGGER AS $$
DECLARE
    target_arc_id UUID;
    arc legacy_placement_adoption_arc%ROWTYPE;
    totals RECORD;
    progress JSONB;
BEGIN
    target_arc_id := COALESCE((to_jsonb(NEW) ->> 'arc_id')::UUID, (to_jsonb(NEW) ->> 'id')::UUID);
    SELECT * INTO arc FROM legacy_placement_adoption_arc WHERE id = target_arc_id;
    IF NOT FOUND OR arc.audit_version = 0 THEN RETURN NULL; END IF;

    SELECT COUNT(*) AS passes,
           COALESCE(SUM(evidence_examined_delta), 0) AS evidence_examined,
           COALESCE(SUM(evidence_resolved_delta), 0) AS evidence_resolved,
           COALESCE(SUM(evidence_confirmed_delta), 0) AS evidence_confirmed,
           COALESCE(SUM(mint_examined_delta), 0) AS mint_examined,
           COALESCE(SUM(available_delta), 0) AS available,
           COALESCE(SUM(missing_delta), 0) AS missing,
           COALESCE(SUM(corrupt_delta), 0) AS corrupt,
           COALESCE(SUM(already_recorded_delta), 0) AS already_recorded,
           COALESCE(SUM(conflicts_delta), 0) AS conflicts,
           COALESCE(SUM(retryable_delta), 0) AS retryable,
           COALESCE(SUM(read_bytes_delta), 0) AS read_bytes,
           COUNT(*) FILTER (WHERE yield_reason IN ('ByteBudget', 'TimeBudget')) AS budget_yields,
           COUNT(*) FILTER (WHERE oversized_item) AS oversized_passes
    INTO totals FROM legacy_placement_adoption_pass_audit audit WHERE audit.arc_id = arc.id;

    IF (arc.completed_passes, arc.evidence_examined, arc.evidence_resolved, arc.evidence_confirmed,
        arc.mint_examined, arc.available, arc.missing, arc.corrupt, arc.already_recorded, arc.conflicts,
        arc.retryable, arc.read_bytes, arc.budget_yields, arc.oversized_passes)
       IS DISTINCT FROM
       (totals.passes, totals.evidence_examined, totals.evidence_resolved, totals.evidence_confirmed,
        totals.mint_examined, totals.available, totals.missing, totals.corrupt, totals.already_recorded, totals.conflicts,
        totals.retryable, totals.read_bytes, totals.budget_yields, totals.oversized_passes) THEN
        RAISE EXCEPTION 'legacy adoption cumulative counters must equal append-only pass audit (arc=%).', arc.id;
    END IF;
    IF arc.completed_passes > 0 AND NOT EXISTS (
        SELECT 1 FROM legacy_placement_adoption_pass_audit audit
        WHERE audit.arc_id = arc.id AND audit.claim_token = arc.last_settled_claim_token
    ) THEN
        RAISE EXCEPTION 'legacy adoption last settled claim must identify a durable pass audit (arc=%, claim=%).',
            arc.id, arc.last_settled_claim_token;
    END IF;

    IF arc.final_summary_jsonb IS NOT NULL AND arc.state IN ('Cleaning', 'Completed', 'Expired', 'Stale') THEN
        progress := arc.final_summary_jsonb -> 'Progress';
        IF progress IS NULL OR
           (progress ->> 'MemberCount')::BIGINT IS DISTINCT FROM arc.member_count OR
           (progress ->> 'EvidenceExamined')::BIGINT IS DISTINCT FROM arc.evidence_examined OR
           (progress ->> 'EvidenceResolved')::BIGINT IS DISTINCT FROM arc.evidence_resolved OR
           (progress ->> 'EvidenceConfirmed')::BIGINT IS DISTINCT FROM arc.evidence_confirmed OR
           (progress ->> 'MintExamined')::BIGINT IS DISTINCT FROM arc.mint_examined OR
           (progress ->> 'Available')::BIGINT IS DISTINCT FROM arc.available OR
           (progress ->> 'Missing')::BIGINT IS DISTINCT FROM arc.missing OR
           (progress ->> 'Corrupt')::BIGINT IS DISTINCT FROM arc.corrupt OR
           (progress ->> 'AlreadyRecorded')::BIGINT IS DISTINCT FROM arc.already_recorded OR
           (progress ->> 'Conflicts')::BIGINT IS DISTINCT FROM arc.conflicts OR
           (progress ->> 'Retryable')::BIGINT IS DISTINCT FROM arc.retryable OR
           (progress ->> 'ReadBytes')::BIGINT IS DISTINCT FROM arc.read_bytes OR
           (progress ->> 'CompletedPasses')::BIGINT IS DISTINCT FROM arc.completed_passes OR
           (progress ->> 'BudgetYields')::BIGINT IS DISTINCT FROM arc.budget_yields OR
           (progress ->> 'OversizedPasses')::BIGINT IS DISTINCT FROM arc.oversized_passes THEN
            RAISE EXCEPTION 'legacy adoption terminal summary must exactly reproduce durable cumulative audit (arc=%).', arc.id;
        END IF;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER legacy_placement_adoption_arc_require_audit_shape
    AFTER INSERT OR UPDATE ON legacy_placement_adoption_arc
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION legacy_placement_adoption_require_audit_shape();

CREATE CONSTRAINT TRIGGER legacy_placement_adoption_pass_require_audit_shape
    AFTER INSERT ON legacy_placement_adoption_pass_audit
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION legacy_placement_adoption_require_audit_shape();

COMMENT ON TABLE legacy_placement_adoption_pass_audit IS
    'Secret-free append-only result of one claimed provider pass. One PK row per claim; renewals are folded into that pass. Unclaimed Cleaning is not audited. Deleted only with its retained terminal arc.';
