-- 0097_publish_manifest_pull_request.sql
--
-- Publish-or-park PR-6: the pull/merge request reference for a publish_manifest row, once the Room's Open-PR action
-- (or a workflow's git.open_pr/git.open_change_set node) opens one for its branch. Both nullable — a manifest row is
-- written the moment a branch is pushed, long before any PR exists; these fill in once one is opened. Doubles as the
-- idempotency read for the Room action: a row with a non-null pull_request_url already has an open PR, so a repeat
-- click reuses it instead of opening a duplicate. Additive: two new nullable columns on an existing table.

ALTER TABLE publish_manifest ADD COLUMN IF NOT EXISTS pull_request_number INT;
ALTER TABLE publish_manifest ADD COLUMN IF NOT EXISTS pull_request_url TEXT;
