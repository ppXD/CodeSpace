-- Engine v2 — workflow→chat (closed-loop review request). A per-team "CodeSpace" bot identity
-- authors workflow-posted messages (review cards, digests) so a run with no human actor (e.g. a
-- PR-triggered run) can still post into chat with a stable, attributable author. Flag the bot rows.
-- Default false → every existing user is human (non-breaking).

ALTER TABLE app_user ADD COLUMN is_bot boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN app_user.is_bot IS
    'True for a non-human identity (the per-team CodeSpace bot that authors workflow-posted chat messages). Bots have no password and never sign in.';
