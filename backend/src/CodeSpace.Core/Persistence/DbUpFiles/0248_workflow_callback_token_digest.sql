-- 0248_workflow_callback_token_digest.sql
--
-- A flow.wait_callback wait is resumed by an anonymous POST to /api/workflows/callbacks/{token}, so its token is a
-- bearer credential, and workflow_run_wait.token holds it in plaintext, readable by anyone with database or backup
-- access. This is the EXPAND step of moving it out of plaintext. It adds two columns and changes no existing value:
--
--   token_digest      the token's lowercase hex SHA-256, which this version looks a Callback wait up by (the way
--                     invitation and reset tokens are kept). Filled below for every Callback row holding a minted token.
--   token_ciphertext  the token AES-GCM encrypted (base64) under the deployment's variable master key
--                     (Variables:MasterKey, read from CODESPACE_VARIABLE_MASTER_KEY), opened only by the reveal that needs
--                     runs.control. That key is held by the environment and never by this database -- unlike the Data
--                     Protection key ring, which sits in data_protection_keys. SQL cannot reach the key, so only rows this
--                     version mints get one.
--
-- token itself is left as it is, and this version keeps writing the minted token there, because a pod on the previous
-- build looks a callback up with `token = <presented>`. So:
--   * Any rollout order works -- API or worker first, pod by pod -- and a rollback to the previous build keeps every
--     callback URL resuming, including URLs this version issued.
--   * The digest is not a key anywhere: the previous build compares the presented value with token, which never holds a
--     digest, and this version compares the digest OF the presented value. (Both route only 32 characters since
--     a0eb90933; builds before it route any segment to that comparison.)
--   * A Callback row the previous build parks after this script ran has no digest and no ciphertext; this version still
--     resumes it by its token column and reveals it from there.
--
-- CONTRACT STEP (a later release, not this script): only once no pod older than this version can run. It must first fill
-- token_digest where it is still NULL (rows previous-version pods parked after this script) and seal token_ciphertext in
-- the application, which holds the key; then set token to '' for Callback rows and stop writing it there. Never write
-- the digest, the projection placeholder, or any other value a callback route can carry into token: every build before
-- the contract step compares whatever is presented with that column.
--
-- Both columns are NULLABLE with NO DEFAULT: catalog-only ADD COLUMNs. The UPDATE only fills a NULL digest for the minted
-- shape (32 lowercase hex), so running the script twice changes nothing. The partial index covers the one lookup that
-- reads the digest: a pending Callback wait by token_digest.

ALTER TABLE workflow_run_wait ADD COLUMN IF NOT EXISTS token_digest TEXT NULL;
ALTER TABLE workflow_run_wait ADD COLUMN IF NOT EXISTS token_ciphertext TEXT NULL;

COMMENT ON COLUMN workflow_run_wait.token_digest IS
    'A Callback wait''s token as lowercase hex SHA-256, which a resume looks the wait up by. NULL on other wait kinds, and on Callback rows a pre-0248 build parks.';

COMMENT ON COLUMN workflow_run_wait.token_ciphertext IS
    'A Callback wait''s token, AES-GCM encrypted (base64) under the deployment''s environment-held variable master key, opened only by the reveal that needs runs.control. NULL on other wait kinds, and on Callback rows minted before 0248 or parked by a pre-0248 build.';

UPDATE workflow_run_wait
SET token_digest = encode(sha256(convert_to(token, 'UTF8')), 'hex')
WHERE wait_kind = 'Callback'
  AND token_digest IS NULL
  AND token ~ '^[0-9a-f]{32}$';

CREATE INDEX IF NOT EXISTS idx_wrw_pending_callback_token_digest
    ON workflow_run_wait (token_digest)
    WHERE status = 'Pending' AND wait_kind = 'Callback';
