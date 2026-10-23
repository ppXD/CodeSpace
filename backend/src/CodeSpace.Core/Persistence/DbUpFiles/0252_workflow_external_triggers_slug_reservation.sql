-- 0252_workflow_external_triggers_slug_reservation.sql
-- The management route /api/workflows/external-triggers is a literal GET sibling of /api/workflows/{idOrSlug}.
-- A workflow already holding that slug becomes unreachable, so move each live row to the same -N scheme the
-- runtime allocator uses, preserving soft-deleted rows because their slug is audit history.

WITH reserved AS (
    SELECT id, team_id, slug
    FROM workflow
    WHERE slug = 'external-triggers' AND deleted_date IS NULL
),
candidates AS (
    SELECT r.id, r.team_id, r.slug, n
    FROM reserved r
    CROSS JOIN LATERAL generate_series(2, 1000) AS n
    WHERE NOT EXISTS (
        SELECT 1
        FROM workflow w
        WHERE w.team_id = r.team_id
          AND w.slug = 'external-triggers-' || n::text
          AND w.deleted_date IS NULL
    )
)
UPDATE workflow w
SET slug = 'external-triggers-' || c.n::text
FROM candidates c
WHERE w.id = c.id
  AND c.n = (
      SELECT MIN(c2.n)
      FROM candidates c2
      WHERE c2.id = c.id
  );
