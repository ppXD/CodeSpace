-- 0245_app_user_email_unique_ignoring_case.sql
--
-- One live account per address, compared ignoring letter case — where the data already allows it.
--
-- Sign-in compares addresses ignoring case, so two live accounts at Alice@corp and alice@corp make that address
-- ambiguous. 0001's idx_app_user_email is unique on the raw spelling only. This index closes the gap at the database
-- for every deployment whose accounts are already distinct ignoring case, which is every deployment created through
-- invitations (they store addresses lower-cased).
--
-- This script NEVER fails. A migration that refuses stops the deployment from starting, which is an outage, and
-- choosing which of two duplicate accounts keeps an address is choosing whose memberships, credentials and history
-- survive — no rule this script could apply is safe to apply silently. So over existing duplicates it creates no
-- index, changes no row, and raises a WARNING naming the count, how to list them, and the statement to run once an
-- operator has merged or soft-deleted (set deleted_date on) all but one account per address.
--
-- The application does not depend on this index. It refuses to create an account at an address a live account
-- already holds in any letter case, and sign-in resolves an address exactly as typed first, then ignoring case only
-- when that names a single account — so the accounts of an existing duplicate pair keep signing in with their own
-- spelling, and an ambiguous one signs in nobody.
--
-- Soft-deleted rows are outside the index, as they are outside 0001's: sign-in never resolves them, and an address
-- freed by deleting an account can be invited again. 0001's index stays.

DO $$
DECLARE
    duplicated_addresses BIGINT;
BEGIN
    SELECT count(*)
      INTO duplicated_addresses
      FROM (
          SELECT lower(email)
            FROM app_user
           WHERE deleted_date IS NULL
           GROUP BY lower(email)
          HAVING count(*) > 1
      ) AS duplicated;

    IF duplicated_addresses = 0 THEN
        EXECUTE 'CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_email_lower ON app_user (lower(email)) WHERE deleted_date IS NULL';
        RETURN;
    END IF;

    RAISE WARNING '0245: % address(es) are held by more than one live account when compared ignoring letter case, so the case-insensitive unique index ux_app_user_email_lower was NOT created. Nothing was changed and the deployment continues; each of those accounts still signs in with its own exact address.', duplicated_addresses
        USING HINT = 'List them with: SELECT lower(email) AS address, array_agg(id ORDER BY created_date) AS accounts FROM app_user WHERE deleted_date IS NULL GROUP BY lower(email) HAVING count(*) > 1; then merge or soft-delete all but one per address and run: CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_email_lower ON app_user (lower(email)) WHERE deleted_date IS NULL;';
END
$$;
