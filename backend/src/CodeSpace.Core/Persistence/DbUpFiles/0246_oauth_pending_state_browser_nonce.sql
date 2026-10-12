-- 0246_oauth_pending_state_browser_nonce.sql
--
-- Binds a pending OAuth connect to the browser that started it.
--
-- The callback is anonymous by necessity, and until now the state token was its only proof: whichever browser
-- presented it finished the connect. An admin of one team could start a connect on their own OAuth app, forward the
-- authorize link, and receive the recipient's provider token as a credential in their team — together with an act-as
-- identity naming the recipient's provider account. /init now sets a random nonce in an HttpOnly cookie in the
-- starting browser and stores its SHA-256 here; the callback consumes a row only when state AND hash both match, so
-- a callback from any other browser consumes nothing.
--
-- Nullable on purpose, for the rolling deploy: a pod still on the previous version inserts rows without the column,
-- and a NOT NULL would turn every connect it starts into a 500 until it is replaced. Such a row can never be completed
-- by this version — NULL equals no hash — and the expiry sweep removes it within its ten-minute lifetime.

ALTER TABLE oauth_pending_state ADD COLUMN IF NOT EXISTS browser_nonce_hash TEXT NULL;

COMMENT ON COLUMN oauth_pending_state.browser_nonce_hash IS
    'Hex SHA-256 of the nonce the starting browser holds in its HttpOnly binding cookie. The callback must present that nonce; NULL only on a row a previous version wrote, which can never be completed.';
