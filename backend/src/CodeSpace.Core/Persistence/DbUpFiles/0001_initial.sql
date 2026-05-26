-- 0001_initial.sql
-- CodeSpace initial schema: User / Team / Repository binding model.

CREATE TABLE app_user (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email               TEXT NOT NULL,
    name                TEXT NOT NULL,
    avatar_url          TEXT,
    password_hash       TEXT,
    last_login_date     TIMESTAMPTZ,

    created_date        TIMESTAMPTZ NOT NULL,
    created_by          UUID NOT NULL,
    last_modified_date  TIMESTAMPTZ NOT NULL,
    last_modified_by    UUID NOT NULL,
    deleted_date        TIMESTAMPTZ
);

CREATE UNIQUE INDEX idx_app_user_email ON app_user(email) WHERE deleted_date IS NULL;


CREATE TABLE team (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    slug                TEXT NOT NULL,
    name                TEXT NOT NULL,
    description         TEXT,
    owner_user_id       UUID NOT NULL REFERENCES app_user(id),

    created_date        TIMESTAMPTZ NOT NULL,
    created_by          UUID NOT NULL,
    last_modified_date  TIMESTAMPTZ NOT NULL,
    last_modified_by    UUID NOT NULL,
    deleted_date        TIMESTAMPTZ
);

CREATE UNIQUE INDEX idx_team_slug ON team(slug) WHERE deleted_date IS NULL;


CREATE TABLE team_membership (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id             UUID NOT NULL REFERENCES team(id),
    user_id             UUID NOT NULL REFERENCES app_user(id),
    role                TEXT NOT NULL CHECK (role IN ('Owner','Admin','Member','Viewer')),

    created_date        TIMESTAMPTZ NOT NULL,
    created_by          UUID NOT NULL,
    last_modified_date  TIMESTAMPTZ NOT NULL,
    last_modified_by    UUID NOT NULL,

    UNIQUE (team_id, user_id)
);


CREATE TABLE provider_instance (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),
    provider                    TEXT NOT NULL CHECK (provider IN ('GitHub','GitLab','Git')),
    display_name                TEXT NOT NULL,
    base_url                    TEXT NOT NULL,
    api_url                     TEXT,
    web_url                     TEXT,

    oauth_client_id             TEXT,
    oauth_client_secret_enc     TEXT,
    oauth_redirect_path         TEXT,
    oauth_default_scopes        TEXT[],

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,
    deleted_date                TIMESTAMPTZ,

    UNIQUE (team_id, provider, base_url)
);


CREATE TABLE credential (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),
    provider_instance_id        UUID NOT NULL REFERENCES provider_instance(id),
    owner_user_id               UUID REFERENCES app_user(id),

    auth_type                   TEXT NOT NULL CHECK (auth_type IN (
                                    'Pat','ProjectAccessToken','GroupAccessToken',
                                    'OAuth','GitHubApp','SshKey','BasicAuth'
                                )),
    display_name                TEXT NOT NULL,
    encrypted_payload           TEXT NOT NULL,
    scopes                      TEXT[],
    expires_date                TIMESTAMPTZ,
    last_validated_date         TIMESTAMPTZ,
    status                      TEXT NOT NULL DEFAULT 'Active'
                                  CHECK (status IN ('Active','Expired','Revoked','Error')),
    last_error                  TEXT,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,
    deleted_date                TIMESTAMPTZ
);

CREATE INDEX idx_credential_team_active
    ON credential(team_id) WHERE deleted_date IS NULL AND status = 'Active';


CREATE TABLE repository (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),
    provider_instance_id        UUID NOT NULL REFERENCES provider_instance(id),
    credential_id               UUID REFERENCES credential(id),

    external_id                 TEXT NOT NULL,
    namespace_path              TEXT NOT NULL,
    name                        TEXT NOT NULL,
    full_path                   TEXT NOT NULL,
    default_branch              TEXT NOT NULL DEFAULT 'main',
    visibility                  TEXT NOT NULL DEFAULT 'Private'
                                  CHECK (visibility IN ('Public','Internal','Private')),
    description                 TEXT,
    web_url                     TEXT NOT NULL,
    clone_url_https             TEXT,
    clone_url_ssh               TEXT,
    archived                    BOOLEAN NOT NULL DEFAULT FALSE,

    last_synced_date            TIMESTAMPTZ,
    last_event_date             TIMESTAMPTZ,
    status                      TEXT NOT NULL DEFAULT 'Active'
                                  CHECK (status IN ('Active','Paused','Error','Unreachable')),
    last_error                  TEXT,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,
    deleted_date                TIMESTAMPTZ,

    UNIQUE (provider_instance_id, external_id)
);

CREATE INDEX idx_repository_team_active
    ON repository(team_id) WHERE deleted_date IS NULL;


CREATE TABLE repository_webhook (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    repository_id               UUID NOT NULL REFERENCES repository(id),
    external_id                 TEXT NOT NULL,
    callback_url                TEXT NOT NULL,
    secret_enc                  TEXT NOT NULL,
    subscribed_events           TEXT[] NOT NULL,
    active                      BOOLEAN NOT NULL DEFAULT TRUE,
    last_received_date          TIMESTAMPTZ,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,

    UNIQUE (repository_id, external_id)
);
