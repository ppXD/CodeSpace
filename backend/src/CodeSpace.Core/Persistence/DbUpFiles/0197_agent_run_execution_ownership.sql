-- Nullable additions preserve historical rows. Deploy after draining older writers; old binaries do not enforce these API fences.
ALTER TABLE agent_run ADD COLUMN owner_id uuid NULL;
ALTER TABLE agent_run ADD COLUMN reattach_reservation_id uuid NULL;
ALTER TABLE agent_run ADD CONSTRAINT ck_agent_run_owner_nonempty CHECK (owner_id IS NULL OR owner_id <> '00000000-0000-0000-0000-000000000000');
ALTER TABLE agent_run ADD CONSTRAINT ck_agent_run_reservation_nonempty CHECK (reattach_reservation_id IS NULL OR reattach_reservation_id <> '00000000-0000-0000-0000-000000000000');
ALTER TABLE agent_run_event ADD COLUMN writer_kind text NOT NULL DEFAULT 'legacy';
ALTER TABLE agent_run_event ADD COLUMN writer_owner_id uuid NULL;
ALTER TABLE agent_run_event ADD COLUMN writer_epoch bigint NULL;
ALTER TABLE agent_run_event ADD CONSTRAINT ck_agent_run_event_writer CHECK ((writer_kind = 'worker' AND writer_owner_id IS NOT NULL AND writer_epoch IS NOT NULL) OR (writer_kind IN ('legacy', 'system') AND writer_owner_id IS NULL AND writer_epoch IS NULL));
