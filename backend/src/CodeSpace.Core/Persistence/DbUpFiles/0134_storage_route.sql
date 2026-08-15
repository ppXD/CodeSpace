-- 0134_storage_route.sql
--
-- Additive team-scoped routing policy for versioned data classes. A route chooses the storage profile used by a new
-- write; each durable artifact location still stamps an exact storage profile revision, so changing a route never
-- changes historical read semantics. Runtime routing and ArtifactStore cutover are intentionally outside this slice.
--
-- 0133 is reserved by the independent Agent Run log capture-source migration and must land before this script.

CREATE TABLE storage_route (
    id                   UUID         NOT NULL PRIMARY KEY,
    team_id              UUID         NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    data_class_type_key  VARCHAR(128) NOT NULL,
    current_revision     INTEGER      NOT NULL,
    state                VARCHAR(16)  NOT NULL,
    created_date         TIMESTAMPTZ  NOT NULL,
    created_by           UUID         NOT NULL,
    last_modified_date   TIMESTAMPTZ  NOT NULL,
    last_modified_by     UUID         NOT NULL,

    CONSTRAINT ak_storage_route_team_id UNIQUE (team_id, id),
    CONSTRAINT ck_storage_route_current_revision CHECK (current_revision > 0),
    CONSTRAINT ck_storage_route_data_class_type_key CHECK (data_class_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'),
    CONSTRAINT ck_storage_route_state CHECK (state IN ('Draft', 'Active', 'Disabled', 'Retired'))
);

CREATE UNIQUE INDEX ux_storage_route_team_data_class ON storage_route (team_id, data_class_type_key);
CREATE INDEX ix_storage_route_team_state_data_class ON storage_route (team_id, state, data_class_type_key);

CREATE TABLE storage_route_revision (
    id                       UUID         NOT NULL PRIMARY KEY,
    team_id                  UUID         NOT NULL,
    storage_route_id         UUID         NOT NULL,
    revision                 INTEGER      NOT NULL,
    storage_profile_id       UUID         NOT NULL,
    profile_revision_mode    VARCHAR(24)  NOT NULL,
    pinned_profile_revision  INTEGER      NULL,
    created_date             TIMESTAMPTZ  NOT NULL,
    created_by               UUID         NOT NULL,

    CONSTRAINT fk_storage_route_revision_route FOREIGN KEY (team_id, storage_route_id)
        REFERENCES storage_route (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_storage_route_revision_profile FOREIGN KEY (team_id, storage_profile_id)
        REFERENCES storage_profile (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_storage_route_revision_pinned_profile FOREIGN KEY (team_id, storage_profile_id, pinned_profile_revision)
        REFERENCES storage_profile_revision (team_id, storage_profile_id, revision) ON DELETE RESTRICT,
    CONSTRAINT ck_storage_route_revision_number CHECK (revision > 0),
    CONSTRAINT ck_storage_route_revision_profile_selection CHECK (
        (profile_revision_mode = 'CurrentAtWrite' AND pinned_profile_revision IS NULL)
        OR (profile_revision_mode = 'Pinned' AND pinned_profile_revision IS NOT NULL AND pinned_profile_revision > 0)),
    CONSTRAINT ux_storage_route_revision_number UNIQUE (team_id, storage_route_id, revision)
);

CREATE INDEX ix_storage_route_revision_team_profile_created
    ON storage_route_revision (team_id, storage_profile_id, created_date, id);

ALTER TABLE storage_route
    ADD CONSTRAINT fk_storage_route_current_revision
    FOREIGN KEY (team_id, id, current_revision)
    REFERENCES storage_route_revision (team_id, storage_route_id, revision)
    ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;

CREATE OR REPLACE FUNCTION storage_route_revision_guard() RETURNS trigger AS $$
DECLARE
    route_row storage_route%ROWTYPE;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION
            'storage_route_revision is immutable — % rejected (route_id=%, revision=%). Append a new revision instead.',
            TG_OP, OLD.storage_route_id, OLD.revision;
    END IF;

    SELECT * INTO route_row FROM storage_route
    WHERE team_id = NEW.team_id AND id = NEW.storage_route_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN NEW;
    END IF;

    IF route_row.state = 'Retired' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7501',
            MESSAGE = format('storage_route Retired state rejects new revisions (id=%s).', route_row.id);
    END IF;

    IF NEW.revision <> route_row.current_revision AND NEW.revision <> route_row.current_revision + 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7501',
            MESSAGE = format('storage_route revision must be current or the next revision (id=%s, current=%s, attempted=%s).',
                route_row.id, route_row.current_revision, NEW.revision);
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_route_revision_enforce_append_only
    BEFORE INSERT OR UPDATE OR DELETE ON storage_route_revision
    FOR EACH ROW EXECUTE FUNCTION storage_route_revision_guard();

CREATE OR REPLACE FUNCTION storage_route_revision_require_current() RETURNS trigger AS $$
DECLARE
    pointed_revision INTEGER;
    final_state VARCHAR(16);
BEGIN
    SELECT current_revision, state INTO pointed_revision, final_state FROM storage_route
    WHERE team_id = NEW.team_id AND id = NEW.storage_route_id;

    IF final_state = 'Retired' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7501',
            MESSAGE = format('storage_route cannot append a revision in a transaction whose final state is Retired (route_id=%s, appended=%s).',
                NEW.storage_route_id, NEW.revision);
    END IF;

    IF pointed_revision IS DISTINCT FROM NEW.revision THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P7501',
            MESSAGE = format('storage_route revision append must atomically advance current_revision (route_id=%s, appended=%s, current=%s).',
                NEW.storage_route_id, NEW.revision, pointed_revision);
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER storage_route_revision_require_current_at_commit
    AFTER INSERT ON storage_route_revision
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION storage_route_revision_require_current();

CREATE OR REPLACE FUNCTION storage_route_guard_identity() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.current_revision <> 1 OR NEW.state <> 'Draft' THEN
            RAISE EXCEPTION
                'storage_route must start at revision 1 in Draft state (id=%, revision=%, state=%).',
                NEW.id, NEW.current_revision, NEW.state;
        END IF;
        RETURN NEW;
    END IF;

    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'storage_route is durable identity — DELETE rejected (id=%). Move state to Retired instead.', OLD.id;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.data_class_type_key IS DISTINCT FROM OLD.data_class_type_key
       OR NEW.created_date IS DISTINCT FROM OLD.created_date OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'storage_route stable identity is immutable (id=%).', OLD.id;
    END IF;

    IF NEW.current_revision < OLD.current_revision OR NEW.current_revision > OLD.current_revision + 1 THEN
        RAISE EXCEPTION
            'storage_route current_revision advances exactly once (id=%, old=%, new=%).',
            OLD.id, OLD.current_revision, NEW.current_revision;
    END IF;

    IF OLD.state = 'Retired' AND NEW.state <> 'Retired' THEN
        RAISE EXCEPTION 'storage_route Retired state is terminal (id=%, attempted_state=%).', OLD.id, NEW.state;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER storage_route_guard_stable_identity
    BEFORE INSERT OR UPDATE OR DELETE ON storage_route
    FOR EACH ROW EXECUTE FUNCTION storage_route_guard_identity();
