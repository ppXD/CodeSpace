-- P2a (v4.2-FINAL Lock Clause 1): the completion protocol's per-run policy identity. Both columns are stamped
-- IMMUTABLY at run creation (RunStarter / RunFromSnapshotStarter, same transaction as the row) and never updated:
-- a replay/rerun is a NEW execution stamped with the policy current at ITS creation. NULL on both = a Legacy
-- (pre-protocol) run -- the composer projects LegacyUnknown and never re-derives old tape into contract truth.
-- completion_enforcement_mode: 'Legacy' | 'Shadow' | 'Enforced' (text; readers parse FAIL-CLOSED, unknown -> Legacy).
-- 'Enforced' is only ever written by P2b's qualified-cohort rollout -- generic creation stamps 'Shadow', under
-- which the assessment is composed and recorded but the run's terminal status is NEVER mutated (Lock Clause 1:
-- production terminal mutation has exactly one owner, and it is not the Shadow phase).
-- Rollback: ALTER TABLE workflow_run DROP COLUMN completion_policy_version, DROP COLUMN completion_enforcement_mode;
ALTER TABLE workflow_run ADD COLUMN completion_policy_version int NULL;
ALTER TABLE workflow_run ADD COLUMN completion_enforcement_mode text NULL;
