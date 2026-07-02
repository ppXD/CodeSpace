-- 0092_work_plan_assumptions_questions.sql
--
-- Plan contract enrichment (triad S2a): the plan can now carry the producer's recorded ASSUMPTIONS (defaults
-- chosen where the goal was ambiguous — the Codex-style plan contract) and operator QUESTIONS (each with
-- mutually exclusive options + a recommended default — the confirm-form's fodder, rendered by the S3 gate).
-- Both optional jsonb; every existing row reads NULL = absent. Purely additive + idempotent.

ALTER TABLE work_plan
    ADD COLUMN IF NOT EXISTS assumptions_json JSONB NULL,
    ADD COLUMN IF NOT EXISTS questions_json JSONB NULL;
