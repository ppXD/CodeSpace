-- 0250_oauth_pending_state_replace_credential.sql
--
-- Lets a pending OAuth connect name a credential to re-authorize in place.
--
-- Every reconnect used to mint a NEW credential row, and everything bound through the old one — repositories, group
-- hooks, the act-as identity — pointed at a row whose token was dead until a whole succession subsystem moved it. A
-- connect that names the credential it replaces writes the new token into that row instead: same id, nothing to move.
-- /init stores the credential's id here; the callback reads it back and re-authorizes that row.
--
-- Nullable and additive, for the rolling deploy: a pod still on the previous version inserts rows without the column
-- (NULL here) and ignores it on the rows a newer pod wrote — it then completes the connect as a new credential, exactly as
-- it always did. A row with NULL is a plain connect for every version.
--
-- No foreign key, on purpose. A pending row lives ten minutes and is swept by the expiry job; the reference is checked
-- again against (id, team) when the callback uses it, because the credential can be retired in between, and a hard
-- constraint would only make a row that can never complete a reason for something unrelated to fail.

ALTER TABLE oauth_pending_state ADD COLUMN IF NOT EXISTS replace_credential_id UUID NULL;

COMMENT ON COLUMN oauth_pending_state.replace_credential_id IS
    'The credential this connect re-authorizes in place (its token is replaced, the id kept); NULL connects a new credential. Not a foreign key: re-checked against (id, team) when the callback uses it.';
