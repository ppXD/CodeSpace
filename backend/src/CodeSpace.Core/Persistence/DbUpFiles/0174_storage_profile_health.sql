-- The last thing a probe observed about a storage profile's destination.
--
-- A SEPARATE ROW rather than columns on storage_profile, and that is not tidiness: storage_profile carries an xmin
-- optimistic-concurrency token that every operator edit checks against. A background probe writing into that row would
-- advance the token and make an operator's next Save fail with a conflict nobody caused. Health is observed ABOUT the
-- profile, never BY it.
--
-- Exactly one row per profile: the question a settings screen asks is "does my storage work right now", not "how has it
-- behaved". An append-only history would answer a question nobody is asking yet, and would need its own retention.
CREATE TABLE storage_profile_health (
    team_id             UUID         NOT NULL,
    storage_profile_id  UUID         NOT NULL,

    -- Which revision was exercised. A profile whose revision has since moved on has health that describes a
    -- destination it no longer uses, and a reader must be able to see that rather than trust a stale green.
    profile_revision    INT          NOT NULL,

    status              VARCHAR(16)  NOT NULL,

    -- Whether the probe actually wrote. A read-only probe that passes says the credential can list, not that a run's
    -- bytes will land — the two are different claims and a screen must not render them the same.
    write_verified      BOOLEAN      NOT NULL,

    failure_stage       VARCHAR(32)  NULL,
    failure_code        VARCHAR(64)  NULL,
    latency_ms          BIGINT       NOT NULL,
    observed_at         TIMESTAMPTZ  NOT NULL,

    CONSTRAINT pk_storage_profile_health PRIMARY KEY (team_id, storage_profile_id),
    CONSTRAINT fk_storage_profile_health_profile FOREIGN KEY (team_id, storage_profile_id)
        REFERENCES storage_profile (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_storage_profile_health_status CHECK (status IN ('Available', 'ReadOnly', 'Degraded', 'Unavailable', 'Cancelled')),
    CONSTRAINT ck_storage_profile_health_revision CHECK (profile_revision > 0),
    CONSTRAINT ck_storage_profile_health_latency CHECK (latency_ms >= 0),

    -- A failing status must say WHY, and a passing one must not pretend to. An operator reading 'Unavailable' with no
    -- code has been told the destination is broken and nothing about which end to fix.
    CONSTRAINT ck_storage_profile_health_failure CHECK (
        (status = 'Available' AND failure_stage IS NULL AND failure_code IS NULL)
        OR (status <> 'Available' AND failure_stage IS NOT NULL AND failure_code IS NOT NULL))
);

CREATE INDEX ix_storage_profile_health_stale ON storage_profile_health (observed_at);

COMMENT ON TABLE storage_profile_health IS
    'The last probe observation for one storage profile. Written by the probe recorder, never by an operator edit; kept off storage_profile so a background probe cannot advance that row''s concurrency token.';
COMMENT ON COLUMN storage_profile_health.write_verified IS
    'True when the probe PUT and discarded a real object. A read-only pass qualifies the credential''s ability to list, which is a weaker claim than "a run''s bytes will land here".';
COMMENT ON COLUMN storage_profile_health.profile_revision IS
    'The revision the probe exercised. Compare against storage_profile.current_revision: health for an older revision describes a destination the profile has since left.';
