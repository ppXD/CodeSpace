-- Bounded prompt-memory lifetime. Legacy rows receive the same server-owned 30-day lifetime measured from the
-- evidence time already stored in valid_from; elapsed rows remain auditable and can be explicitly superseded.
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS expires_at timestamptz NULL;

UPDATE lesson SET expires_at = valid_from + INTERVAL '30 days' WHERE expires_at IS NULL;

ALTER TABLE lesson ALTER COLUMN expires_at SET NOT NULL;
ALTER TABLE lesson DROP CONSTRAINT IF EXISTS ck_lesson_expiry;
ALTER TABLE lesson ADD CONSTRAINT ck_lesson_expiry CHECK (expires_at > valid_from);

CREATE INDEX IF NOT EXISTS ix_lesson_team_mode_eligible
    ON lesson (team_id, mode, expires_at DESC)
    WHERE invalidated_at IS NULL;

COMMENT ON COLUMN lesson.expires_at IS
    'Server-owned prompt eligibility bound. Expiry removes a lesson from readers without deleting its provenance.';
