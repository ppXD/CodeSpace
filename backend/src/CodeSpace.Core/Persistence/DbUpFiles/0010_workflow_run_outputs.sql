-- 0010_workflow_run_outputs.sql
-- Adds the workflow's declared OUTPUTS bag to workflow_run. When a Terminal node succeeds,
-- the engine resolves the Terminal's Inputs map (each key maps to a {{ref}} pointing at
-- some upstream value) and writes the result here. External consumers (API callers, future
-- sub-workflow nodes) read this column to get "what did this run produce".
--
-- Default '{}' so existing rows stay valid AND workflows without any declared Outputs run
-- cleanly (the Terminal writes an empty object, callers get an empty object back).

ALTER TABLE workflow_run
    ADD COLUMN IF NOT EXISTS outputs_jsonb JSONB NOT NULL DEFAULT '{}'::jsonb;
