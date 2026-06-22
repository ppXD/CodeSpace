-- 0067_workflow_run_kinds.sql
--
-- Two semantic-classification columns for runs filtering:
--
--  * run_kind — the COARSE origin kind, PURELY a function of source_type, so it is a Postgres GENERATED column: the DB
--    derives it for every row (existing + future) with zero population code and no drift. (A generated STORED column
--    rewrites the table on ADD; acceptable here — the runs table is small at this stage. At scale this would instead be
--    a denorm column set at the creation sites, like source_type 0062.)
--  * projection_kind — the projection / coordination MODE of a task run (single-agent / plan-map-synth / supervisor /
--    …), which is NOT derivable from any column (it lives in the snapshot's node graph), so it is a real nullable
--    denorm column threaded in from the projection layer at the snapshot creation site. NULL for an authored / non-task
--    run.
--
-- Both filtered by `= ANY`, recheck-tier on the team keyset (no dedicated index until such a surface proves hot —
-- same stance as status / source / actor). Idempotent.

ALTER TABLE workflow_run
    ADD COLUMN IF NOT EXISTS run_kind text GENERATED ALWAYS AS (
        CASE
            WHEN source_type = 'manual'        THEN 'workflow'
            WHEN source_type = 'snapshot'      THEN 'task'
            WHEN source_type IN ('replay', 'rerun') THEN 'replay'
            WHEN source_type = 'schedule.cron' THEN 'schedule'
            WHEN source_type = 'workflow.child' THEN 'child'
            WHEN source_type = 'api'           THEN 'api'
            WHEN source_type LIKE 'provider.%' OR source_type LIKE 'trigger.%' THEN 'event'
            ELSE 'other'
        END
    ) STORED;

ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS projection_kind text;

COMMENT ON COLUMN workflow_run.run_kind IS
    'GENERATED from source_type — the coarse origin kind (workflow/task/event/replay/schedule/child/api/other). Read-only.';
