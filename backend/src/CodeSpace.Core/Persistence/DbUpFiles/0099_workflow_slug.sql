-- 0099_workflow_slug.sql
-- Give every workflow a stable, team-unique slug so it can be addressed by a clean URL
-- (/teams/{team}/workflows/{slug}) instead of a raw GUID — mirroring project / agent / skill.
-- Additive + backfilled + non-breaking.
--
-- Unlike project.slug (a variable-path contract key that REFUSES on collision), a workflow
-- slug is display-only, so creation auto-suffixes (-2, -3) via WorkflowService.DeriveAvailableSlugAsync.

-- 1) Nullable first so the backfill can populate it.
ALTER TABLE workflow ADD COLUMN IF NOT EXISTS slug VARCHAR(64);

-- 2) Format CHECK mirrors project/agent slug shape. The C# generator emits lowercase kebab; the
--    wider [A-Za-z0-9_-] set is allowed for legacy/hand-set values. NULL is permitted only for the
--    transient backfill window below (removed by the NOT NULL in step 4).
ALTER TABLE workflow DROP CONSTRAINT IF EXISTS chk_workflow_slug_format;
ALTER TABLE workflow ADD CONSTRAINT chk_workflow_slug_format
    CHECK (slug IS NULL OR slug ~ '^[A-Za-z0-9_-]{1,64}$');

-- 3) Backfill EVERY existing row (alive + soft-deleted) so the column can go NOT NULL. The slugify
--    expression mirrors ProjectService.SlugifyName / AgentDefinitionService.DeriveSlug: lowercase,
--    keep [a-z0-9_], collapse every other run to a single '-', trim leading/trailing '-', cap 64
--    (re-trimming a '-' the cut may expose), and map an empty result to 'workflow'. Rows are then
--    deduped per (team_id, base_slug) with a '-N' suffix (N = ordinal within the group), so no two
--    rows in a team ever share a slug and the partial unique index in step 4 cannot fail.
-- Loop-until-free per row, byte-mirroring the runtime resolver WorkflowService.DeriveAvailableSlugAsync:
-- a `-N` variant is bumped until it collides with NOTHING already assigned in the team, so a variant can
-- never duplicate another row's base slug (a pure ROW_NUMBER partition would emit "foo-2" for both
-- "Foo","Foo" (partition foo, rn 2) AND "Foo 2" (partition foo-2, rn 1) → unique-index build aborts the
-- whole DbUp batch). Reserved words that would be shadowed by a literal /api/workflows GET route are also
-- pushed to `-N`. Row-by-row (one-time backfill), so the O(rows) EXISTS probe is acceptable.
DO $$
DECLARE
    r          RECORD;
    candidate  TEXT;
    n          INT;
BEGIN
    FOR r IN
        SELECT id, team_id,
               CASE WHEN base = '' THEN 'workflow' ELSE base END AS base_slug
        FROM (
            SELECT id, team_id, created_date,
                   RTRIM(LEFT(TRIM(BOTH '-' FROM regexp_replace(lower(name), '[^a-z0-9_]+', '-', 'g')), 64), '-') AS base
            FROM workflow
            WHERE slug IS NULL
        ) s
        ORDER BY team_id, created_date, id
    LOOP
        candidate := r.base_slug;
        n := 1;
        WHILE candidate IN ('runs', 'node-manifests', 'system-variables', 'decisions')
           OR EXISTS (SELECT 1 FROM workflow w
                      WHERE w.team_id = r.team_id AND w.slug = candidate AND w.deleted_date IS NULL AND w.id <> r.id)
        LOOP
            n := n + 1;
            candidate := LEFT(r.base_slug, 64 - LENGTH('-' || n::text)) || '-' || n::text;
        END LOOP;
        UPDATE workflow SET slug = candidate WHERE id = r.id;
    END LOOP;
END $$;

-- 4) Every row now has a slug — enforce NOT NULL + per-team uniqueness among alive rows
--    (a soft-deleted slug is reusable, matching project/agent/skill).
ALTER TABLE workflow ALTER COLUMN slug SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_team_slug_active
    ON workflow(team_id, slug) WHERE deleted_date IS NULL;
