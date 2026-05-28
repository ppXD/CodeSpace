-- 0028_chat_foundation.sql
--
-- Team chat foundation — the bottom-most layer for in-app collaboration. One unified
-- conversation model behind DM / group / channel (Slack-style), UUID-v7 time-sortable
-- message ids for cursor pagination at scale, a generic message_reference table that lets
-- @-anything (user / PR / workflow / code-location / future) work with zero schema churn,
-- and a generated tsvector + GIN index so full-text search is available from day one
-- (back-filling FTS onto a populated hot table later is the painful path).
--
-- No real-time transport here — SignalR rides on top in a later migration-free PR. The
-- schema is designed so polling and push read the SAME indexes.
--
-- Idempotency: every statement is IF NOT EXISTS guarded so a re-run on an already-applied
-- environment is a no-op.

-- ─── conversation ────────────────────────────────────────────────────────────
-- kind: 'Direct' | 'Group' | 'Channel' (stored as the enum's string form via EF
-- HasConversion<string>). slug/name null for DM; visibility only meaningful for Channel.
CREATE TABLE IF NOT EXISTS conversation (
    id                  UUID         NOT NULL PRIMARY KEY,
    team_id             UUID         NOT NULL REFERENCES team(id),
    kind                TEXT         NOT NULL,
    slug                TEXT         NULL,
    name                TEXT         NULL,
    description         TEXT         NULL,
    visibility          TEXT         NOT NULL DEFAULT 'Public',
    archived            BOOLEAN      NOT NULL DEFAULT FALSE,
    created_date        TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by          UUID         NOT NULL,
    last_modified_date  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by    UUID         NOT NULL,
    deleted_date        TIMESTAMPTZ  NULL
);

-- Channel slug unique per team (alive + present). NULLs (DM/group) don't collide.
CREATE UNIQUE INDEX IF NOT EXISTS uq_conversation_team_slug
    ON conversation(team_id, slug)
    WHERE slug IS NOT NULL AND deleted_date IS NULL;

CREATE INDEX IF NOT EXISTS idx_conversation_team_active
    ON conversation(team_id)
    WHERE deleted_date IS NULL;

-- ─── conversation_member ─────────────────────────────────────────────────────
-- Composite PK (conversation_id, user_id). last_read_message_id is the per-member read
-- cursor — unread = messages with id > last_read_message_id (UUID v7 ids are comparable
-- by creation time). One row per (user, conversation): no per-message read-receipt
-- explosion.
CREATE TABLE IF NOT EXISTS conversation_member (
    conversation_id       UUID         NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
    user_id               UUID         NOT NULL,
    team_id               UUID         NOT NULL,
    role                  TEXT         NOT NULL DEFAULT 'Member',
    last_read_message_id  UUID         NULL,
    muted                 BOOLEAN      NOT NULL DEFAULT FALSE,
    joined_date           TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_date          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by            UUID         NOT NULL,
    last_modified_date    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by      UUID         NOT NULL,
    deleted_date          TIMESTAMPTZ  NULL,
    PRIMARY KEY (conversation_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_conversation_member_user_active
    ON conversation_member(user_id)
    WHERE deleted_date IS NULL;

CREATE INDEX IF NOT EXISTS idx_conversation_member_team_active
    ON conversation_member(team_id)
    WHERE deleted_date IS NULL;

-- ─── message ─────────────────────────────────────────────────────────────────
-- id is UUID v7 (generated app-side via Guid.CreateVersion7()) — time-sortable, so the
-- (conversation_id, id) index IS the chronological order. No separate seq, no timestamp
-- sort. body holds markdown + inline reference tokens; references are also denormalised
-- into message_reference for reverse lookup. search_tsv is a generated column so the app
-- never writes it and it can never drift from body.
CREATE TABLE IF NOT EXISTS message (
    id                   UUID         NOT NULL PRIMARY KEY,
    conversation_id      UUID         NOT NULL REFERENCES conversation(id) ON DELETE CASCADE,
    team_id              UUID         NOT NULL,
    author_user_id       UUID         NOT NULL,
    body                 TEXT         NOT NULL,
    reply_to_message_id  UUID         NULL,
    created_date         TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    edited_date          TIMESTAMPTZ  NULL,
    deleted_date         TIMESTAMPTZ  NULL,
    search_tsv           TSVECTOR     GENERATED ALWAYS AS (to_tsvector('simple', body)) STORED
);

-- Cursor-pagination backbone: latest-N + before/after-id within a conversation, all one
-- index. DESC matches the dominant "newest first" read.
CREATE INDEX IF NOT EXISTS idx_message_conversation_cursor
    ON message(conversation_id, id DESC);

-- Full-text search. 'simple' config = no stemming/stopwords — chat is code-heavy + multi-
-- lingual, so exact-token matching beats English stemming here. Swap the config later if
-- a language-aware variant is wanted; the GIN index rebuilds without app changes.
CREATE INDEX IF NOT EXISTS idx_message_search_tsv
    ON message USING GIN(search_tsv);

-- ─── message_reference ───────────────────────────────────────────────────────
-- The generic @ system. ref_type is an OPEN string namespace (user / pull_request /
-- workflow / code_location / anything) — a new reference kind is a string value, never a
-- migration. ref_metadata caches the display label + deep-link hint per ref so rendering
-- doesn't re-resolve.
CREATE TABLE IF NOT EXISTS message_reference (
    id            UUID         NOT NULL PRIMARY KEY,
    message_id    UUID         NOT NULL REFERENCES message(id) ON DELETE CASCADE,
    team_id       UUID         NOT NULL,
    ref_type      TEXT         NOT NULL,
    ref_id        TEXT         NOT NULL,
    ref_metadata  JSONB        NULL,
    created_date  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Forward: a message's own chips.
CREATE INDEX IF NOT EXISTS idx_message_reference_message
    ON message_reference(message_id);

-- Reverse: "every message in team T that references (ref_type, ref_id)" — backlinks on a
-- PR page, the @mention inbox, workflow-talk aggregation. Leads with team_id for tenancy.
CREATE INDEX IF NOT EXISTS idx_message_reference_target
    ON message_reference(team_id, ref_type, ref_id);

COMMENT ON TABLE conversation IS
    'Unified chat container: DM / group / channel (kind discriminator). One model so the '
    'message / membership / read-cursor machinery is shared across all chat surfaces.';
COMMENT ON TABLE message IS
    'Chat message. id is UUID v7 (time-sortable) — the (conversation_id, id) index is the '
    'cursor-pagination + chronological-order backbone. search_tsv is a generated column.';
COMMENT ON TABLE message_reference IS
    'Generic @-reference reverse index. ref_type is an open string namespace; a new '
    'reference kind (user / PR / workflow / code-location / future) needs zero migration.';
