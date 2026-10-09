-- 0244_webhook_claim.sql
--
-- A key held for a window, so that "this already happened moments ago" is settled by the database instead of by a read
-- that two deliveries landing together can both pass. Two webhook-ingress decisions ride it:
--
--   body:{hook}:{sha256}           A signed GitHub body this hook accepted, held by its X-GitHub-Delivery id. GitHub's
--                                  signature covers the body only, so a captured body stays valid for as long as the
--                                  secret does; posting it again under a fresh delivery id is a replay. The same id
--                                  (a provider redelivery) re-claims it and is deduplicated further on, per activation.
--   pr-debounce:{activation}:{repository}:{number}:{head}
--                                  The pull request head commit that last started a run from this activation. A
--                                  close/reopen loop re-sends a head that already ran, so it starts one run inside the
--                                  window, not one per reopen; a push moves the head, so new commits always run.
--
-- A claim is taken inside the delivery's own transaction, so a delivery that rolls back releases it and the provider's
-- retry is not refused, and it touches no row but its own key. An expired claim is taken over in place by the next claim
-- on its key; the recurring WebhookClaimPurgeRecurringJob deletes the rest, outside any delivery. (A delivery that
-- purged expired rows itself held their locks until it committed, and two such deliveries could deadlock.)
--
-- holder is TEXT, not a bounded VARCHAR: it is the delivery id from an unsigned header, and a value too long for a
-- column would fail the delivery with a 500 instead of being refused or deduplicated.
--
-- New table, no backfill. Idempotent (IF NOT EXISTS). An older pod never reads or writes it.

CREATE TABLE IF NOT EXISTS webhook_claim (
    claim_key  TEXT         PRIMARY KEY,
    holder     TEXT         NOT NULL,
    claimed_at TIMESTAMPTZ  NOT NULL,
    expires_at TIMESTAMPTZ  NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_webhook_claim_expires_at ON webhook_claim (expires_at);

COMMENT ON TABLE webhook_claim IS
    'A key held until expires_at by one holder: a replayed webhook body (body:) or a debounced pull request head (pr-debounce:). Taken in the delivery transaction; expired rows are taken over in place by a claim on the same key and deleted by a recurring sweep.';
