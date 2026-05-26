-- 0007_provider_instance_per_oauth_app.sql
-- Relax the (team_id, provider, base_url) uniqueness so a single team can register
-- multiple OAuth apps pointing at the same host. Common reason: one app with broader
-- scopes for admins, another with narrower scopes for read-only members; or separate
-- apps purely for audit-trail separation (the app name appears in the user's "authorized
-- apps" page on the provider). Two providers with the SAME (team, provider, base_url)
-- AND the SAME oauth_client_id are still a duplicate — that's an accidental re-add.
--
-- COALESCE(oauth_client_id, '') is what lets us still enforce "only one PAT-only /
-- not-yet-configured provider per (team, host)" — Postgres unique indexes treat NULL as
-- distinct, which would silently allow unlimited oauth-less rows.

ALTER INDEX IF EXISTS idx_provider_instance_team_provider_url_active RENAME TO idx_provider_instance_team_provider_url_active_legacy;
DROP INDEX IF EXISTS idx_provider_instance_team_provider_url_active_legacy;

CREATE UNIQUE INDEX idx_provider_instance_team_provider_url_client_active
    ON provider_instance (team_id, provider, base_url, COALESCE(oauth_client_id, ''))
    WHERE deleted_date IS NULL;
