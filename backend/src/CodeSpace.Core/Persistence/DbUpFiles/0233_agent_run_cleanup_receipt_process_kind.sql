-- 0233_agent_run_cleanup_receipt_process_kind.sql
--
-- An abandon's KILL could be skipped, and nothing anywhere recorded that it was.
--
-- 0229 gave every host-local resource of an abandoned run its own row — spool, netns, cgroup leaf, socket, clone,
-- credential lease — and deliberately left one out: the supervised agent PROCESS itself. It was left out because it
-- looked like the resource the abandon path definitely handled: TerminateAsync was always called, and the reconciler
-- logged if it threw.
--
-- It does not throw. The local durable runner WITHHOLDS the kill, returning normally, in four situations it alone can
-- see: it cannot bind the handle to a launch it is willing to act on (including a transient IOException while
-- re-reading the four native-launch files), the launch belongs to another host or another boot of this one, liveness
-- cannot be observed at all, or the tree is still alive when the bounded reap wait expires. In every one of those the
-- run reached Failed while its agent kept running — holding its workspace and burning its injected model credential —
-- and the only trace was a Linux CI test that could say no more than "should be True but was False".
--
-- So the process becomes a kind like the others, and the terminate's typed outcome becomes its error_code:
-- terminate-skipped-not-local / -unresolvable-handle / -indeterminate, terminate-timed-out-waiting-reap, and
-- terminate-threw for the caller's own catch (a runner that could not reach a decision is not one of its decisions).
-- error_code carries no CHECK of its own — 0229 constrains only kind and outcome — so this vocabulary grows without a
-- migration. An observed kill (or an already-dead tree) is Completed; a withheld one is Unknown, which is exactly what
-- is then true: nobody can say whether that agent is still running.
--
-- Not Orphaned: an orphan is addressed to a sweep on the owning host, and AgentRunOrphanReaper has no teardown for a
-- process (its resolver answers null for any kind it cannot act on). Claiming an orphan nobody sweeps would be the
-- same silence in a louder font.
--
-- And precisely BECAUSE no sweep settles it, this kind is excluded from the Room's unknown-resource count, exactly as
-- the credential lease already is (RoomProjector.SummarizeRecovery via RunCleanupReceipt.IsBeyondEverySweep). A row
-- nothing can ever clear would otherwise pin a recovery card on the turn forever — and a cross-host abandon cannot
-- read the foreign spool, so it takes a withheld-kill path on essentially every run it abandons. The receipt is for
-- DIAGNOSIS and the ledger: the operator's query, and what a failing kill assertion reads back to name its own cause.
-- The condition itself closes unobservably, at the wall-clock deadline whose passing is what made the abandon safe.
--
-- Rollback: the two statements below, with 'Process' removed from the IN list. Any Process rows must be deleted first
-- (there is no other kind they could honestly become).

ALTER TABLE agent_run_cleanup_receipt DROP CONSTRAINT ck_agent_run_cleanup_receipt_kind;

ALTER TABLE agent_run_cleanup_receipt ADD CONSTRAINT ck_agent_run_cleanup_receipt_kind CHECK (
    kind IN ('Spool', 'LogSegments', 'McpSocket', 'EgressSubnet', 'Cgroup', 'Workspace', 'ProviderCredentialLease', 'Process'));
