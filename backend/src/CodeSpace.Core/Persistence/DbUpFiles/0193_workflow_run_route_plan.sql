-- Route provenance: the FULL routing decision a task launch was projected from, denormalised beside
-- projection_kind (migration 0067). projection_kind alone answers "which builder ran"; it drops the effort tier,
-- the recipe, the bounds preset + caps, whether the classifier (not the operator) chose the tier, its confidence,
-- the rationale and any capability degrade — so a reader could never say WHY a run got the depth it got. NULL for
-- an authored / non-task run (there is no route) and for task runs staged before this column existed.
ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS route_plan_jsonb jsonb NULL;
