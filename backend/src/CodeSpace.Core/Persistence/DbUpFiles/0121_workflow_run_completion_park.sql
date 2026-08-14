-- P4 (v4.3, "NeedsReview must be a durable Park"): when the completion authority parks a run at the terminal
-- boundary (Suspended + a completion-authority reason), that park is DELIBERATE -- a human's to look at -- yet it
-- wears the exact shape the stuck-run reconciler treats as STRANDED (Suspended, no pending wait), so every sweep
-- re-dispatched it into a re-walk -> re-arbitrate -> re-park churn loop (each cycle paying a full compose plus a
-- live handoff probe). This stamp is the discriminator: the authority sets it on every terminal park, the
-- reconciler's stranded sweep skips stamped rows, and the operator's Continue verb clears it as THE one
-- deliberate re-arbitration channel (fix the contract world, then continue -> the replayed walk re-arbitrates
-- against the then-current facts). Cleared on any arbitrated terminal stamp.
-- Rollback: ALTER TABLE workflow_run DROP COLUMN completion_parked_at;
ALTER TABLE workflow_run ADD COLUMN completion_parked_at timestamptz NULL;
