-- 0145_workflow_run_model_call_harness_observed.sql
--
-- Makes the model-call plane able to carry a call an agent CLI made INSIDE itself, HONESTLY. A workflow LLM node's
-- calls reach 0124's tables because they pass through ILLMClient and a recording decorator; a harness's own calls never
-- touch either — the CLI talks to the provider itself — so until now the only durable figure for the work agents
-- actually do was one derived per-run token aggregate, and for a harness that reports no usage not even a cost. A frame
-- the CLI printed about its own call is the only possible evidence, and reading a row out of a frame after the fact
-- leaves several columns unobservable. This migration adds the two things such a row needs in order not to lie.
--
-- WHAT unavailable_figures IS FOR. Every figure at stake here is already a nullable column, and NULL alone cannot tell
-- "nobody could observe this" from "nobody has written it yet". For an in-process call the difference is academic; for a
-- harness-observed one it decides whether a cost report is trustworthy. A provider request id the CLI never prints, a
-- token class it does not report, a cost for a model this deployment has no price for — writing zero for any of those
-- reads as measured, and a cost report that is quietly wrong is the one that gets trusted. So the row NAMES them, and
-- the CHECK holds the only contradiction that would make naming them worse than saying nothing: a figure declared
-- unavailable may not also carry a value.
--
-- WHAT AN EMPTY SET MEANS, exactly, because the default puts one on every existing row: that the producer DECLARES
-- nothing unavailable. It does NOT mean every figure on the row was measured. A producer that never populated a column
-- and never declared it says nothing about that column either way, which is precisely what every row written before
-- this column existed says — so the default is honest for them rather than a claim made on their behalf.
--
-- WHAT IS NOT ENFORCED HERE, stated so nobody reads it into the CHECK. Ordering and distinctness of the set are the
-- writer's contract (ModelCallFigures.Canonical), not the database's: a PostgreSQL CHECK cannot contain the subquery
-- that comparing an array against its own sorted-distinct form needs, and a duplicate or unsorted member is untidy
-- rather than untrue. What IS enforced is membership of the seven-name vocabulary and the no-value rule, per figure.
--
-- Every nullable comparison inside the negations is paired with its own IS NOT NULL rather than left bare, and that is
-- load bearing exactly as it is on 0141's redaction arm: a CHECK admits a row when it evaluates to TRUE *or NULL*, so a
-- bare `cost_amount` term would evaluate the arm to NULL for a row with no cost and ADMIT what it exists to refuse.
-- unavailable_figures itself is NOT NULL, so `'x' = ANY(unavailable_figures)` is FALSE for an empty set, never NULL.
--
-- WHAT source_native_record_id IS FOR. The frame a row was read out of is the row's whole evidence, so provenance must
-- be a column rather than a join a reader has to know to make. It is a SOFT reference with no foreign key, matching
-- 0124's stated discipline for its other cross-aggregate ids: telemetry may outlive the frames a run's cleanup removes,
-- and 0139's records are RESTRICT-deleted, so a hard key here would make model-call retention hostage to record
-- retention. The unique index over it states the invariant that actually matters for re-projection: one captured frame
-- evidences AT MOST ONE attempt. It is the second, independent guard — 0130's ux_workflow_run_model_call_source_identity
-- already collapses two frames of one provider response into one logical call.
--
-- WHAT THIS DOES NOT CHANGE. 0130's admission triggers are untouched: a harness-observed attempt names no
-- workflow_run_record, so it takes the existing guard's non-record arm exactly as written (both record ids NULL,
-- source_evidence_revision 0). Nothing here participates in completion, terminal decision, planner, oracle, critic or
-- model routing, and the derived per-run token aggregate on an Agent Run's result is computed exactly as it was.
-- Rollback: DROP INDEX ux_workflow_run_model_call_attempt_source_native_record;
--           ALTER TABLE workflow_run_model_call_attempt
--               DROP CONSTRAINT ck_workflow_run_model_call_attempt_unavailable_figures,
--               DROP CONSTRAINT ck_workflow_run_model_call_attempt_source_native_record,
--               DROP COLUMN unavailable_figures, DROP COLUMN source_native_record_id;

ALTER TABLE workflow_run_model_call_attempt
    ADD COLUMN unavailable_figures    TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN source_native_record_id UUID  NULL,
    ADD CONSTRAINT ck_workflow_run_model_call_attempt_source_native_record CHECK (
        source_native_record_id IS NULL
        OR source_native_record_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    ADD CONSTRAINT ck_workflow_run_model_call_attempt_unavailable_figures CHECK (
        COALESCE(array_ndims(unavailable_figures), 1) = 1
        AND array_position(unavailable_figures, NULL::text) IS NULL
        AND unavailable_figures <@ ARRAY['cache_read_tokens', 'cache_write_tokens', 'completed_at', 'cost_amount', 'first_token_at', 'provider_request_id', 'reasoning_tokens']::text[]
        AND NOT ('provider_request_id' = ANY (unavailable_figures) AND provider_request_id IS NOT NULL)
        AND NOT ('cache_read_tokens' = ANY (unavailable_figures) AND cache_read_tokens IS NOT NULL)
        AND NOT ('cache_write_tokens' = ANY (unavailable_figures) AND cache_write_tokens IS NOT NULL)
        AND NOT ('reasoning_tokens' = ANY (unavailable_figures) AND reasoning_tokens IS NOT NULL)
        AND NOT ('cost_amount' = ANY (unavailable_figures) AND cost_amount IS NOT NULL)
        AND NOT ('first_token_at' = ANY (unavailable_figures) AND first_token_at IS NOT NULL)
        AND NOT ('completed_at' = ANY (unavailable_figures) AND completed_at IS NOT NULL));

CREATE UNIQUE INDEX ux_workflow_run_model_call_attempt_source_native_record
    ON workflow_run_model_call_attempt (team_id, workflow_run_id, source_native_record_id)
    WHERE source_native_record_id IS NOT NULL;

COMMENT ON COLUMN workflow_run_model_call_attempt.unavailable_figures IS
    'Figures this row''s producer DECLARES it could not produce, named by their own column names. Each named column is NULL rather than zero, enforced. An empty set declares nothing; it does not claim every figure was measured.';

COMMENT ON COLUMN workflow_run_model_call_attempt.source_native_record_id IS
    'The captured native frame a harness-observed row was read out of. Soft reference, unique per attempt: one frame evidences at most one attempt. NULL for a producer that read no frame.';
