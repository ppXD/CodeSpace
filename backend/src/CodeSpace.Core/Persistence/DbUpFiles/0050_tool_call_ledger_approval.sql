-- 0050_tool_call_ledger_approval.sql
--
-- Item D (durable mid-turn HITL) approval sub-state. An approval records a DECISION without yet running the side
-- effect: the human's approve verdict stamps approved_by_user_id + approved_at while the row STAYS AwaitingApproval —
-- the blocked tool-call handler is the one that flips it to a terminal once it executes the side effect (D2). The
-- reject verdict, by contrast, drives AwaitingApproval → Failed directly (no side effect to run).
--
-- approved_at IS NULL distinguishes a not-yet-decided AwaitingApproval row from an approved-but-not-yet-executed one:
-- the D3 reaper will only expire rows that are still AwaitingApproval AND approved_at IS NULL (a stamped-but-unexecuted
-- approval is in flight, not abandoned). Additive + non-breaking: two nullable columns on the existing ledger table,
-- nothing else touched. Idempotent (IF NOT EXISTS).

ALTER TABLE tool_call_ledger ADD COLUMN IF NOT EXISTS approved_by_user_id UUID        NULL;   -- item D — the human who approved; NULL until approved
ALTER TABLE tool_call_ledger ADD COLUMN IF NOT EXISTS approved_at         TIMESTAMPTZ NULL;   -- item D — when approved; NULL distinguishes not-yet-decided from approved-but-unexecuted

COMMENT ON COLUMN tool_call_ledger.approved_by_user_id IS
    'Item D (durable HITL) — the human who approved this parked call. NULL until approved.';

COMMENT ON COLUMN tool_call_ledger.approved_at IS
    'Item D (durable HITL) — when the call was approved. NULL distinguishes a not-yet-decided AwaitingApproval row from '
    'an approved-but-not-yet-executed one; the D3 reaper expires only AwaitingApproval rows where approved_at IS NULL.';
