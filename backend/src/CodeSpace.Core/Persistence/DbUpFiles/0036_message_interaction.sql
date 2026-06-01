-- Engine v2 — interactive messages (closed-loop review request). A message MAY carry an optional
-- polymorphic interaction document: action buttons today; a form / poll / composite (a Children
-- array for combined layouts) later, each as a new json `kind` with ZERO migration. The document
-- also holds the response target (server-side only) + resolution. NULL = a plain message, so every
-- existing message reads back unchanged (non-breaking).

ALTER TABLE message ADD COLUMN interaction_json jsonb NULL;

COMMENT ON COLUMN message.interaction_json IS
    'Optional polymorphic interaction (action_buttons; future form/poll/composite via json kind) + its workflow-wait target (server-side only) + resolution. NULL = a plain message.';
