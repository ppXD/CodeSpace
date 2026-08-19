-- 0146_workflow_run_data_completeness.sql
--
-- The last two structural tables the run data contract declares and the codebase does not have, and they only work
-- together. workflow_run_data_manifest is the COMPLETENESS STATEMENT — what a run's record is expected to contain,
-- what is present, what is known-missing, and a verdict. workflow_run_capture_gap is the other half: the place a
-- producer records "I know I missed something HERE".
--
-- WHY NEITHER IS WORTH SHIPPING ALONE. Without the gap plane a gap is invisible, and an invisible gap is
-- indistinguishable from no gap: a manifest computed over a plane that cannot represent its own absences would report
-- complete because nothing said otherwise. Without the manifest a gap is a row nobody has to reconcile with any claim.
-- Together they turn "the data is complete" from an impression into a claim with a definite meaning, an owner, and a
-- refusable failure mode.
--
-- WHAT THE MANIFEST DOES WHEN IT CANNOT TELL, WHICH IS THE WHOLE POINT OF THE TABLE. The verdict answers exactly one
-- question — may this run's record be read as complete? — and only the two states WorkflowRunCaptureCompleteness
-- already calls strictly readable answer yes:
--   * Exact          -> COMPLETE. Expectation determinate, everything expected present, nothing known-missing.
--   * RedactedExact  -> COMPLETE. The same, with secret spans masked; a redacted record is still a whole one.
--   * Partial        -> NOT complete. Something is known-missing.
--   * Unavailable    -> NOT complete. It was never captured.
--   * Corrupt        -> NOT complete. Present and unreadable.
--   * LegacyUnknown  -> NOT complete. THE INDETERMINATE ARM: nobody could establish what should be here.
-- An indeterminate is therefore an expected_record_count of NULL, and ck_..._completeness refuses either complete
-- verdict over it. That asymmetry is the table's entire value: a manifest that read complete because it could not
-- check would convert an unknown into a false assurance, which is worse than having no manifest at all.
-- What is enforced is the DIRECTION ("an indeterminate can never read as complete"), not the SPELLING: a producer
-- that cannot tell may say LegacyUnknown or, if it also knows some span is missing, Partial. Both are honest
-- not-complete answers and no CHECK can pick between them.
--
-- COMPUTABLE WITHOUT SCANNING A RUN'S RECORDS. The manifest is a MATERIALIZED statement that producers advance as
-- they capture, never a query evaluated on read. Deciding a verdict touches four bounded facts: the two counters on
-- the row, whether expected_record_count is stated at all, and one index probe for an open gap
-- (ix_workflow_run_capture_gap_open). No plane is scanned. The alternative — COUNT(*) per run over every run-owned
-- plane — is unevaluatable at scale precisely where it matters most: workflow_run_native_record and
-- agent_run_log_segment grow with harness traffic, so a talkative run's manifest would cost a full scan of its two
-- largest tables every time anyone asked.
-- What incremental counters CANNOT establish is whether a producer that died between durably writing a record and
-- advancing the counter left the counters short. Both halves of that window fail CLOSED and that is deliberate: a
-- producer that wrote the record but not the counter leaves present < expected (not complete), and one that never
-- declared an expectation at all leaves expected NULL (indeterminate, not complete). Neither can read as complete.
--
-- AND THE RESIDUE THAT IS NOT CLOSED, because choosing incremental counters is choosing it. Both counts are the
-- PRODUCER'S declarations, and nothing here compares them to the planes they describe: a writer that declares
-- expected 0 / present 0 and states Exact for a facet holding five hundred rows is refused by nothing in this
-- migration. Checking that IS the full scan this definition exists to avoid, so the database cannot hold it and no
-- comment here should suggest otherwise. What the database does hold is everything reachable without the scan — an
-- indeterminate cannot read as complete, a shortfall cannot, and a known-missing span un-completes the statement
-- whichever order the two writers arrive in. The rest is the producing slice's to earn, and its tests'.
--
-- THE GAP IS ALWAYS ADMITTED, AND THE MANIFEST IS WHAT MOVES. A gap INSERT is never refused on the grounds that some
-- manifest already claims the run is complete — refusing the honest observation to protect the claim is the exact
-- inversion this plane exists to prevent. Instead workflow_run_capture_gap_mark_manifest downgrades every strictly
-- readable manifest row of that run to Partial, so the ORDER the two writers arrive in cannot decide the outcome.
-- Neither writer can see the other's uncommitted row, so EVERY write on either side takes one per-run advisory
-- transaction lock: gap INSERT, manifest INSERT, and manifest UPDATE alike. A manifest UPDATE is not exempt because it
-- holds its own row's lock — the downgrade only matches manifest rows its own snapshot shows as complete or
-- same-facet, so a row being raised to complete for a DIFFERENT facet is never matched, never locked, and the two
-- writers commit blind to each other: the run ends up Exact beside an open gap with neither of them at fault.
--
-- AND THE DOWNGRADE RECONCILES RATHER THAN INCREMENTS, which is why it is the one FOR EACH STATEMENT trigger here. A
-- producer that records three gaps in a single INSERT is being honest in the most useful way available to it, and a
-- per-row downgrade adds one while the manifest's own floor check already counts all three — so the first row's
-- downgrade lands below the floor, the floor check raises, and the WHOLE statement is lost: three gaps gone, and the
-- complete manifest they contradicted still standing. Losing the bad news to protect the claim is strictly worse than
-- having no guard, so the downgrade reconciles each facet's count to the open gaps the plane actually holds, which is
-- correct at any batch size and can never land under the floor.
--
-- A GAP IS NEVER UNNOTICED. No UPDATE except one fill of the resolution axis, and no DELETE at all. That refusal is
-- load-bearing rather than austere: a deletable gap makes a complete manifest reachable by deleting the evidence.
-- The cost is stated plainly — a gap row is not prunable, and its run is not deletable while it exists, the same
-- dead end workflow_run_native_record already accepted. It is affordable HERE for a reason that does not hold there:
-- this plane's row count is bounded by the number of known-missing spans a run noticed, not by its traffic.
--
-- RESOLUTION EXISTS BECAUSE AN UNCLOSEABLE GAP WOULD MAKE THE MANIFEST FAIL-ALWAYS RATHER THAN FAIL-CLOSED. Some
-- spans are never coming back — the bytes past a capture cap were never taken from anyone — and some are: a torn
-- re-attach whose source still holds the lines is captured on the next pass. If a recovered span went on blocking
-- completeness forever, no run that ever re-attached could be complete, and a verdict nothing can reach is not a
-- verdict — fail-closed would have quietly become fail-always. So a gap may be filled ONCE
-- from Open to Recovered, and only while CITING what now covers it. Every gap is nevertheless BORN Open — the guard
-- refuses any other birth state — so a span cannot be recovered-in-the-same-breath and never appear as missing.
-- What the citation does NOT prove, and no schema can: that the cited row's bytes actually cover the span. The
-- database can demand an attributable claim; it cannot go and read the span. That residue is code review's.
--
-- KEYED like the tool-call plane (0141): workflow_run_id is NOT NULL, so one reader asks every run-owned plane the
-- same question, and a STANDALONE AgentRun's gap has no row here. That is the same named gap 0137/0141 already carry,
-- not an oversight. subject_id, stream_id and recovered_by_id are SOFT references for the same reason artifact ids
-- are: the rows they name arrive through bounded sweepers, so a foreign key would refuse a gap whose subject has not
-- been projected yet — and refusing a gap is never the right answer.
--
-- Nothing in this slice produces, reads, folds or bills a row in either table. In particular no terminal decision,
-- completion assessment, planner, oracle, critic or router reads the manifest: making terminal authority answer to
-- it is a separate, later, deliberate cutover, and the manifest would have to be produced before it could be trusted.
-- Rollback: DROP TABLE workflow_run_data_manifest; DROP TABLE workflow_run_capture_gap;

CREATE TABLE workflow_run_capture_gap (
    id                   UUID          NOT NULL PRIMARY KEY,
    team_id              UUID          NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    workflow_run_id      UUID          NOT NULL,
    subject_kind         VARCHAR(48)   NOT NULL,
    subject_id           VARCHAR(512)  NULL,
    stream_id            UUID          NULL,
    channel              VARCHAR(20)   NULL,
    range_kind           VARCHAR(16)   NOT NULL,
    range_start          BIGINT        NULL,
    range_end            BIGINT        NULL,
    range_started_at     TIMESTAMPTZ   NULL,
    range_ended_at       TIMESTAMPTZ   NULL,
    reason               VARCHAR(24)   NOT NULL,
    reason_detail        VARCHAR(2048) NULL,
    capture_source       VARCHAR(64)   NOT NULL,
    noticed_at           TIMESTAMPTZ   NOT NULL,
    resolution           VARCHAR(16)   NOT NULL,
    recovered_at         TIMESTAMPTZ   NULL,
    recovered_by_kind    VARCHAR(48)   NULL,
    recovered_by_id      VARCHAR(512)  NULL,
    schema_version       INTEGER       NOT NULL,
    created_at           TIMESTAMPTZ   NOT NULL,

    CONSTRAINT fk_workflow_run_capture_gap_run FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_capture_gap_bounds CHECK (schema_version > 0),
    CONSTRAINT ck_workflow_run_capture_gap_channel CHECK (
        channel IS NULL OR channel IN ('Stdout', 'Stderr', 'Protocol', 'Control', 'SessionState', 'ModelWire', 'ToolWire', 'Hook', 'Metric', 'Debug')),
    -- The FOUR exhaustive, mutually exclusive coordinate systems a missing span can be stated in, so a producer never
    -- has to invent one and a reader never has to guess which columns mean anything. An Ordinal or ByteOffset span is
    -- refused without its stream_id: a position with nothing to be a position IN is a coordinate nobody can locate.
    -- An open end (range_end / range_ended_at NULL) is deliberately legal — "from here on, and I do not know how much"
    -- is the honest shape of a torn re-attach, and forcing a bound would make a producer guess one.
    --
    -- Every comparison on a NULLABLE column carries its own IS NOT NULL in the same conjunction, and that is
    -- load-bearing rather than defensive: a PostgreSQL CHECK admits a row when it evaluates to TRUE *or NULL*. A bare
    -- `range_end >= range_start` on a NULL start makes its arm NULL, every other arm FALSE, and the whole constraint
    -- NULL — which ADMITS exactly the malformed span it exists to refuse. range_kind is NOT NULL, so the arm selectors
    -- themselves cannot go NULL, and an unlisted range_kind matches no arm and is refused.
    CONSTRAINT ck_workflow_run_capture_gap_range CHECK (
        (range_kind IN ('Ordinal', 'ByteOffset') AND stream_id IS NOT NULL
            AND range_start IS NOT NULL AND range_start >= 0
            AND (range_end IS NULL OR range_end >= range_start)
            AND range_started_at IS NULL AND range_ended_at IS NULL)
        OR (range_kind = 'Time' AND range_started_at IS NOT NULL
            AND (range_ended_at IS NULL OR range_ended_at >= range_started_at)
            AND range_start IS NULL AND range_end IS NULL)
        OR (range_kind = 'Unbounded' AND range_start IS NULL AND range_end IS NULL
            AND range_started_at IS NULL AND range_ended_at IS NULL)),
    -- A closed vocabulary with no escape hatch, which is the difference between a gap and a shrug. There is no
    -- 'Unknown' member: a producer that cannot say why it missed a span has not finished observing it, and a reason
    -- column that admitted 'Unknown' would collect every gap nobody wanted to classify.
    CONSTRAINT ck_workflow_run_capture_gap_reason CHECK (
        reason IN ('BoundExceeded', 'WriteRefused', 'ReattachTorn', 'FrameUnreadable')
        AND (reason_detail IS NULL OR btrim(reason_detail) <> '')),
    -- Recovery is a fill of one axis, and it must CITE what now covers the span. Without the citation, Recovered is an
    -- unattributable claim that silently unblocks a complete verdict.
    CONSTRAINT ck_workflow_run_capture_gap_resolution CHECK (
        (resolution = 'Open' AND recovered_at IS NULL AND recovered_by_kind IS NULL AND recovered_by_id IS NULL)
        OR (resolution = 'Recovered'
            AND recovered_at IS NOT NULL AND recovered_at >= noticed_at
            AND recovered_by_kind IS NOT NULL
            AND recovered_by_kind IN ('model-call', 'model-call-attempt', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'capture-gap', 'data-manifest')
            AND recovered_by_id IS NOT NULL AND btrim(recovered_by_id) <> '')),
    -- What was being captured, named in the run data contract's own owner nouns rather than a parallel vocabulary, so
    -- a gap can always be matched to the plane whose record is missing — which is what lets the manifest count it
    -- against the right facet instead of only suppressing the run globally.
    CONSTRAINT ck_workflow_run_capture_gap_subject CHECK (
        subject_kind IN ('model-call', 'model-call-attempt', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'capture-gap', 'data-manifest')
        AND (subject_id IS NULL OR btrim(subject_id) <> '')
        AND btrim(capture_source) <> ''),
    CONSTRAINT ck_workflow_run_capture_gap_time CHECK (created_at >= noticed_at)
);

-- The manifest's probe, and the reason a verdict costs an index lookup instead of a scan. Partial so it does not grow
-- with recovered spans, and prefixed by the subject so the per-facet count is the same lookup as the run-wide one.
CREATE INDEX ix_workflow_run_capture_gap_open ON workflow_run_capture_gap (team_id, workflow_run_id, subject_kind) WHERE resolution = 'Open';
CREATE INDEX ix_workflow_run_capture_gap_run_noticed ON workflow_run_capture_gap (workflow_run_id, noticed_at, id);
CREATE INDEX ix_workflow_run_capture_gap_team_noticed ON workflow_run_capture_gap (team_id, noticed_at, id);
CREATE INDEX ix_workflow_run_capture_gap_stream ON workflow_run_capture_gap (stream_id, range_start) WHERE stream_id IS NOT NULL;

CREATE TABLE workflow_run_data_manifest (
    id                    UUID        NOT NULL PRIMARY KEY,
    team_id               UUID        NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    workflow_run_id       UUID        NOT NULL,
    facet                 VARCHAR(48) NOT NULL,
    expected_record_count BIGINT      NULL,
    present_record_count  BIGINT      NOT NULL,
    known_missing_count   BIGINT      NOT NULL,
    verdict               VARCHAR(20) NOT NULL,
    revision              BIGINT      NOT NULL,
    schema_version        INTEGER     NOT NULL,
    created_at            TIMESTAMPTZ NOT NULL,
    last_modified_at      TIMESTAMPTZ NOT NULL,

    CONSTRAINT fk_workflow_run_data_manifest_run FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_data_manifest_bounds CHECK (
        present_record_count >= 0 AND known_missing_count >= 0
        AND (expected_record_count IS NULL OR expected_record_count >= 0)
        AND revision > 0 AND schema_version > 0),
    -- THE FAIL-CLOSED ARM. A complete verdict requires a determinate expectation, everything expected present, and
    -- nothing known-missing; anything less is refused rather than rounded up. expected_record_count IS NULL is the
    -- INDETERMINATE case and it lands on the refusing side, because a manifest that reads complete when it could not
    -- check has converted an unknown into a false assurance. Its own IS NOT NULL is what keeps that true: without it
    -- the comparison on a NULL expectation evaluates the arm to NULL, the constraint to NULL, and PostgreSQL admits
    -- exactly the unverifiable complete claim this line exists to refuse.
    -- present > expected is deliberately NOT refused: a re-observed record can legitimately push the present count
    -- past a declared expectation, and a plane that made that unwritable would push producers into not counting.
    CONSTRAINT ck_workflow_run_data_manifest_completeness CHECK (
        verdict NOT IN ('Exact', 'RedactedExact')
        OR (expected_record_count IS NOT NULL AND present_record_count >= expected_record_count
            AND known_missing_count = 0)),
    -- One statement per declared facet of the record, named in the contract's owner nouns. A run-level roll-up is NOT
    -- a column here on purpose: a single count summed across facets cancels — three missing native records against
    -- three surplus model calls would pass present >= expected while the record was plainly incomplete.
    CONSTRAINT ck_workflow_run_data_manifest_facet CHECK (
        facet IN ('model-call', 'model-call-attempt', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'capture-gap', 'data-manifest')),
    CONSTRAINT ck_workflow_run_data_manifest_time CHECK (last_modified_at >= created_at),
    CONSTRAINT ck_workflow_run_data_manifest_verdict CHECK (
        verdict IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown'))
);

-- Two rows stating different completeness for the same facet of the same run is the one shape that would make the
-- table unreadable: whoever asked would have to pick, and picking is what this plane exists to stop.
CREATE UNIQUE INDEX ux_workflow_run_data_manifest_facet ON workflow_run_data_manifest (team_id, workflow_run_id, facet);
CREATE INDEX ix_workflow_run_data_manifest_run ON workflow_run_data_manifest (workflow_run_id, facet);
-- "Whose record is not complete" is the audit's question, and it must not grow with the runs that are fine.
CREATE INDEX ix_workflow_run_data_manifest_incomplete ON workflow_run_data_manifest (team_id, last_modified_at, id) WHERE verdict NOT IN ('Exact', 'RedactedExact');

-- One lock per run, taken by EVERY write that either records a gap or states a completeness verdict — gap INSERT,
-- manifest INSERT and manifest UPDATE alike — so no two of them interleave. A probe cannot see an uncommitted gap and
-- a downgrade cannot see an uncommitted verdict, so without this the two pass against their own snapshots and the run
-- ends up with a complete manifest over an open gap. Both paths acquire it in a BEFORE ROW trigger, which runs before
-- the row it guards is locked, so neither side can be holding a row of one table while waiting for this lock on
-- behalf of the other. Collisions are harmless: the worst one buys is two unrelated writers serializing behind each
-- other. The idiom is BudgetLedger's, which already serializes admissions per run this way.
CREATE OR REPLACE FUNCTION workflow_run_data_completeness_lock(team UUID, run UUID) RETURNS void AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(team::text || ':' || run::text, 0));
END;
$$ LANGUAGE plpgsql;

-- The open-gap floor for ONE facet of one run, in one place, because the downgrade and the manifest's own floor check
-- must agree on it by construction: if the downgrade computed a smaller number than the check, the check would refuse
-- the downgrade and take every gap in the statement down with it. count(*) over an absent facet is 0, never NULL,
-- which is what keeps the floor comparison from evaluating to NULL and admitting the row it exists to refuse.
CREATE OR REPLACE FUNCTION workflow_run_capture_gap_open_count(team UUID, run UUID, subject VARCHAR) RETURNS BIGINT AS $$
    SELECT count(*) FROM workflow_run_capture_gap
    WHERE team_id = team AND workflow_run_id = run AND subject_kind = subject AND resolution = 'Open';
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION workflow_run_capture_gap_guard() RETURNS trigger AS $$
BEGIN
    -- Refusing the DELETE is what makes the manifest's open-gap rule mean anything: a gap that can be removed makes a
    -- complete verdict reachable by deleting the evidence for it. The price is that this row is not prunable and its
    -- run cannot be deleted while it exists.
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow_run_capture_gap is never unnoticed — DELETE rejected (id=%). A removable gap makes a complete manifest reachable by deleting the evidence; recovery is a resolution fill, never a delete.', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.workflow_run_id);

        -- Born Open, always. A gap inserted straight as Recovered would never have been visible as missing, which is
        -- the silence this table exists to break.
        IF NEW.resolution <> 'Open' THEN
            RAISE EXCEPTION 'workflow_run_capture_gap must be born Open (id=%, resolution=%). A span that was never visible as missing is indistinguishable from one that was never missed.', NEW.id, NEW.resolution;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id
       OR NEW.subject_kind IS DISTINCT FROM OLD.subject_kind OR NEW.subject_id IS DISTINCT FROM OLD.subject_id
       OR NEW.stream_id IS DISTINCT FROM OLD.stream_id OR NEW.channel IS DISTINCT FROM OLD.channel
       OR NEW.range_kind IS DISTINCT FROM OLD.range_kind OR NEW.range_start IS DISTINCT FROM OLD.range_start
       OR NEW.range_end IS DISTINCT FROM OLD.range_end
       OR NEW.range_started_at IS DISTINCT FROM OLD.range_started_at
       OR NEW.range_ended_at IS DISTINCT FROM OLD.range_ended_at
       OR NEW.reason IS DISTINCT FROM OLD.reason OR NEW.reason_detail IS DISTINCT FROM OLD.reason_detail
       OR NEW.capture_source IS DISTINCT FROM OLD.capture_source OR NEW.noticed_at IS DISTINCT FROM OLD.noticed_at
       OR NEW.schema_version IS DISTINCT FROM OLD.schema_version OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'workflow_run_capture_gap is append-only apart from its resolution (id=%). Restating what was missing rewrites a fact a reader already audited; a new observation is a new row.', OLD.id;
    END IF;

    -- One fill, one direction. Re-opening a recovered span, or re-recovering an already recovered one under a new
    -- citation, would let the resolution axis be walked until it said what a writer wanted.
    IF OLD.resolution <> 'Open' THEN
        RAISE EXCEPTION 'workflow_run_capture_gap resolution is filled exactly once (id=%, resolution=%).', OLD.id, OLD.resolution;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- FOR EACH ROW, because every check above is a fact about ONE row's own columns — its birth state, and the diff
-- between its OLD and NEW — which a statement-level trigger has no OLD/NEW to read. A malformed row does take its
-- whole statement down with it, and that is the right trade here in a way it is NOT for the downgrade below: the only
-- alternative a BEFORE ROW trigger has is RETURN NULL, which would drop the offending gap in silence, and a silently
-- dropped gap is precisely the silence this table exists to break. Refusing loudly leaves the producer able to retry;
-- dropping quietly does not.
CREATE TRIGGER workflow_run_capture_gap_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_capture_gap
    FOR EACH ROW EXECUTE FUNCTION workflow_run_capture_gap_guard();

-- A noticed gap UN-COMPLETES its run's manifest, and the DATABASE does it rather than the writer that noticed. That is
-- what makes the invariant independent of arrival order: the manifest guard refuses a complete verdict raised over an
-- existing gap, and this trigger downgrades a complete verdict that was already there when the gap arrives. The
-- known-missing count is advanced only on the row whose facet the gap belongs to, so a gap is counted where it
-- happened instead of merely suppressing every verdict in the run.
--
-- AFTER INSERT, deliberately: the floor this reconciles to counts open gaps, and it must be able to see the ones the
-- statement just wrote. RECONCILES rather than increments, for the reason spelled out in the header — a per-row
-- increment lands one below a floor that already counts the whole statement, and the resulting refusal destroys every
-- gap in an honest multi-row INSERT while leaving the complete manifest that contradicted them standing. GREATEST
-- never lowers a count a producer stated above the floor: knowing of more missing than has been rowed errs toward
-- incomplete, which is the safe direction.
CREATE OR REPLACE FUNCTION workflow_run_capture_gap_mark_manifest() RETURNS trigger AS $$
BEGIN
    UPDATE workflow_run_data_manifest AS statement SET
        verdict = CASE WHEN statement.verdict IN ('Exact', 'RedactedExact') THEN 'Partial' ELSE statement.verdict END,
        known_missing_count = GREATEST(statement.known_missing_count,
            workflow_run_capture_gap_open_count(statement.team_id, statement.workflow_run_id, statement.facet)),
        revision = statement.revision + 1,
        last_modified_at = GREATEST(statement.last_modified_at, noticed.latest_at)
    -- GROUP BY, so a statement that inserted NOTHING contributes no row and joins to no manifest. A bare aggregate
    -- would hand back one all-NULL row instead, and a join on a NULL run id matching nothing by accident is not the
    -- same thing as not being asked to match.
    FROM (SELECT team_id, workflow_run_id, max(noticed_at) AS latest_at FROM new_gaps GROUP BY team_id, workflow_run_id) AS noticed
    WHERE statement.team_id = noticed.team_id AND statement.workflow_run_id = noticed.workflow_run_id
      AND (statement.verdict IN ('Exact', 'RedactedExact')
           OR statement.known_missing_count
              < workflow_run_capture_gap_open_count(statement.team_id, statement.workflow_run_id, statement.facet));
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- FOR EACH STATEMENT with a transition table, and the choice is load-bearing rather than stylistic: the manifest's
-- known-missing count has to reconcile with the whole statement's gaps AT ONCE. Per row it cannot — row one's
-- downgrade sees a floor that already counts rows two and three, lands under it, and is refused, which aborts the
-- statement and erases all three admissions. Reconciling once, after every row is visible, is the only shape that is
-- right for a batch of one and a batch of three alike.
CREATE TRIGGER workflow_run_capture_gap_downgrades_its_manifest
    AFTER INSERT ON workflow_run_capture_gap
    REFERENCING NEW TABLE AS new_gaps
    FOR EACH STATEMENT EXECUTE FUNCTION workflow_run_capture_gap_mark_manifest();

CREATE OR REPLACE FUNCTION workflow_run_data_manifest_guard() RETURNS trigger AS $$
DECLARE
    open_gap_id UUID;
    open_facet_gaps BIGINT;
BEGIN
    -- Taken on INSERT *and* UPDATE, and before the open-gap probe below, because that probe is exactly what cannot see
    -- a concurrent uncommitted gap. An UPDATE is not exempt on the grounds that it already holds this row's lock: the
    -- gap's downgrade only matches manifest rows its own snapshot shows as complete or same-facet, so a row being
    -- raised to complete for ANOTHER facet is never matched, never locked, and both writers commit blind — leaving the
    -- run Exact beside an open gap with neither of them at fault. This is the rendezvous both directions need.
    PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.workflow_run_id);

    IF TG_OP = 'INSERT' THEN
        IF NEW.revision <> 1 OR NEW.last_modified_at IS DISTINCT FROM NEW.created_at THEN
            RAISE EXCEPTION 'workflow_run_data_manifest must start as a revision-one statement (id=%).', NEW.id;
        END IF;
    ELSE
        IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
           OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id OR NEW.facet IS DISTINCT FROM OLD.facet
           OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
            RAISE EXCEPTION 'workflow_run_data_manifest stable statement identity is immutable (id=%).', OLD.id;
        END IF;

        IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
            RAISE EXCEPTION 'workflow_run_data_manifest revision must advance exactly once and time must not rewind (id=%, old_revision=%, new_revision=%).', OLD.id, OLD.revision, NEW.revision;
        END IF;
    END IF;

    -- The cross-table half of fail-closed, and the one no CHECK can hold: a complete verdict is refused while ANY
    -- span of the run is still known-missing, whatever facet it belongs to. Conservative on purpose — "something in
    -- this run is missing" is not a fact one facet of the record gets to read past.
    IF NEW.verdict IN ('Exact', 'RedactedExact') THEN
        SELECT id INTO open_gap_id FROM workflow_run_capture_gap
        WHERE team_id = NEW.team_id AND workflow_run_id = NEW.workflow_run_id AND resolution = 'Open'
        ORDER BY noticed_at, id
        LIMIT 1
        FOR SHARE;
        IF FOUND THEN
            RAISE EXCEPTION 'workflow_run_data_manifest cannot claim a complete record while a known-missing span of the run is still open (id=%, facet=%, verdict=%, gap_id=%).', NEW.id, NEW.facet, NEW.verdict, open_gap_id;
        END IF;
    END IF;

    -- ...and the count may not sit BELOW the gaps already rowed for this facet, or the manifest reports less missing
    -- than the plane can already show. Above is admitted: a producer that knows of more missing than it has rowed is
    -- erring toward incomplete, which is the safe direction. This floor is the ONE claim a manifest write may be
    -- refused over, and it shares its definition with the downgrade so the downgrade can never trip it — otherwise
    -- this refusal would kill the gap statement that provoked it, and the gaps are the half that must survive.
    open_facet_gaps := workflow_run_capture_gap_open_count(NEW.team_id, NEW.workflow_run_id, NEW.facet);
    IF NEW.known_missing_count < open_facet_gaps THEN
        RAISE EXCEPTION 'workflow_run_data_manifest known-missing count may not be below the open gaps recorded for this facet (id=%, facet=%, stated=%, open=%).', NEW.id, NEW.facet, NEW.known_missing_count, open_facet_gaps;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- FOR EACH ROW: every check here reads one statement's own OLD/NEW — its identity, its revision step, its verdict
-- against the run's gaps — and the rendezvous lock has to be held before that row's verdict is probed, which only a
-- BEFORE ROW trigger can do. Refusing the statement is also the right direction here, unlike on the gap path: what is
-- being refused is a CLAIM about the record, and a claim is always safe to lose.
CREATE TRIGGER workflow_run_data_manifest_enforce_invariants
    BEFORE INSERT OR UPDATE ON workflow_run_data_manifest
    FOR EACH ROW EXECUTE FUNCTION workflow_run_data_manifest_guard();
