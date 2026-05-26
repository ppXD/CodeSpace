-- 0002_partial_unique_indexes.sql
-- Allow re-binding after soft-delete: convert hard unique constraints on
-- soft-deletable tables into partial unique indexes that ignore tombstones.
-- Without this, INSERT after soft-delete hits 23505 even though the application
-- layer's "active row" check passes.

ALTER TABLE provider_instance DROP CONSTRAINT IF EXISTS provider_instance_team_id_provider_base_url_key;

CREATE UNIQUE INDEX idx_provider_instance_team_provider_url_active
    ON provider_instance (team_id, provider, base_url)
    WHERE deleted_date IS NULL;


ALTER TABLE repository DROP CONSTRAINT IF EXISTS repository_provider_instance_id_external_id_key;

CREATE UNIQUE INDEX idx_repository_provider_instance_external_id_active
    ON repository (provider_instance_id, external_id)
    WHERE deleted_date IS NULL;
