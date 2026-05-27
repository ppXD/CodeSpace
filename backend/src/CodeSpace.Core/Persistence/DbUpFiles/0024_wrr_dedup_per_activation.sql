-- 0024_wrr_dedup_per_activation.sql
--
-- Phase 3.0 hardening — multi-activation fan-out + provider-event idempotency.
--
-- Problem fixed:
--   Migration 0014 created a partial unique index on (source_type, external_event_id) with
--   the intent of deduping duplicate provider deliveries. That's correct when ONE delivery
--   matches ONE activation. It's WRONG when one delivery matches N activations (two
--   workflows in the same team both subscribe to "PR opened in repo Foo"): the dispatcher
--   tries to insert N workflow_run_request rows, all sharing the same delivery id, and the
--   2nd INSERT trips the unique constraint. The 2nd workflow silently doesn't run.
--
-- New model:
--   * (source_type, external_event_id) becomes a regular (non-unique) index — kept for the
--     audit view "show me every request that came from delivery X".
--   * Per-(delivery, activation) dedup is enforced by uq_wrr_idempotency_key (which
--     already exists). RunStarter populates idempotency_key as
--     '{sourceType}:{externalEventId}:{activationId}' for every provider-event request,
--     guaranteeing duplicate deliveries dedup PER ACTIVATION while letting fan-out across
--     activations succeed.
--
-- Why not extend the unique key to include activation_id directly: keeping the audit /
-- diagnostics index separate from the dedup mechanism is cleaner — the diagnostic query
-- ("when was delivery X processed?") doesn't need to know about activations, and the
-- idempotency mechanism is a per-row concern that benefits from the existing
-- uq_wrr_idempotency_key infrastructure (single index, single semantics).

DROP INDEX IF EXISTS uq_wrr_external_event;

CREATE INDEX idx_wrr_external_event
    ON workflow_run_request(source_type, external_event_id)
    WHERE external_event_id IS NOT NULL;

COMMENT ON INDEX idx_wrr_external_event IS
    'Phase 3.0 — non-unique audit index. Replaced the old unique index because multi-'
    'activation fan-out (one webhook delivery matching N activations across N workflows) '
    'needs to create N request rows that all share the same external_event_id. Per-'
    'activation dedup moved to uq_wrr_idempotency_key (RunStarter synthesises '
    'idempotency_key = sourceType:externalEventId:activationId for provider events).';
