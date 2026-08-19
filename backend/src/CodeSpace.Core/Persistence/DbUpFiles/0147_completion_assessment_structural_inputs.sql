-- 0147_completion_assessment_structural_inputs.sql
--
-- The structural inputs the terminal authority gates on, recorded beside the would-be terminal decision so the
-- three gates the shadow does NOT mirror can be re-derived from the row instead of guessed at.
--
--   run_mode              the operating mode RunModeClassifier derived for the run ("supervisor", "plan-map",
--                         "single-agent", "generic"). Resolving it in the mode registry IS the mode-registration
--                         gate; "generic" is deliberately unregistered.
--   capability_key        WHAT the run was asked for, derived from its staked obligation set. Resolving it in the
--                         capability registry IS the capability-registration gate.
--   readiness_at_compose  the ProtocolReadiness the mode's profile held when this row was written; NULL when the
--                         mode had no registered profile. The historical standing, so a later registry edit is
--                         visible as drift rather than silently rewriting what a past run would have got.
--   results_coverage_complete
--                         the run row's own resultsCoverage.complete fact — whether the reduce a budgeted plan-map
--                         synthesized over read ALL of its branches. Recorded, never gated: plan-map holds
--                         ProtocolReadiness.Open, so this is shadow evidence only.
--
-- NULL on every pre-slice row: those rows re-assess on their next sweep and carry the columns from then on.
-- Rollback: ALTER TABLE completion_assessment DROP COLUMN run_mode, DROP COLUMN capability_key,
-- DROP COLUMN readiness_at_compose, DROP COLUMN results_coverage_complete. Idempotent.

ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS run_mode VARCHAR(40);
ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS capability_key VARCHAR(60);
ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS readiness_at_compose VARCHAR(40);
ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS results_coverage_complete BOOLEAN;
