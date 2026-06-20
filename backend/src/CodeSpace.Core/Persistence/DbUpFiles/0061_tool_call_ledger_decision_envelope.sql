-- The parked agent-grain decision.request envelope (the typed DecisionRequest), stashed at park so the cross-grain
-- "Needs decision" queue (Decision substrate D3) can PROJECT it directly, without re-reading the posted card. This
-- mirrors the node-grain stash (workflow_run_wait.payload_jsonb already holds the flow.decision envelope while
-- Pending), making the two backends symmetric for a single unified projection.
--
-- Additive + nullable: a real side-effecting APPROVAL row leaves it NULL (only decision rows set it), and neither the
-- approval CAS nor the decision answer CAS touches it — so this never alters the safety-critical approval columns. The
-- stored envelope is ALREADY REDACTED (the handler runs it through the run's SecretRedactor before persisting), exactly
-- like result_jsonb, so the row is not a new leak surface.
ALTER TABLE tool_call_ledger ADD COLUMN IF NOT EXISTS decision_envelope_jsonb jsonb;
