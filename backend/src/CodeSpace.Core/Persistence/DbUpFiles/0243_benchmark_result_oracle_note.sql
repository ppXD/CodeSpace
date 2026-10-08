-- 0243_benchmark_result_oracle_note.sql
--
-- A benchmark cell's grade carries an oracle-integrity note: the check could not run isolated (every seed cell's
-- root-level `sh check.sh` sources the subject in-process, a shell or node judge starts its commands in the graded
-- tree), the cell changed its judge and the change was voided, or its judge changed while the check ran. The note
-- stopped at the in-memory grade, so the durable row read Solved / "tests-passed" with no trace of it and the
-- scorecards built on these rows could not tell a solve the platform stands behind from one it cannot.
--
-- Additive: one nullable column, no backfill (a row written before this carries no note, which is what it recorded).
-- Idempotent (IF NOT EXISTS). An older pod ignores the column. Not read by the paired-observation admission trigger,
-- which compares its own projected columns only.

ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS oracle_note TEXT NULL;

COMMENT ON COLUMN benchmark_result.oracle_note IS
    'The cell grade''s oracle-integrity note (BenchmarkGrade.OracleNote): an UNVERIFIED check, a voided judge tamper, or a judge changed mid-check. NULL when the grade owed none.';
