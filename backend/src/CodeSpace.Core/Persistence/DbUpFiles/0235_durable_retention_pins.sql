-- 0235_durable_retention_pins.sql
--
-- Two things nothing in the schema could say before: which durable records a sealed qualification result still cites,
-- and when a log stream's bytes became reclaimable.
--
-- WHY A PIN TABLE. A qualification result is an immutable statement ABOUT a set of agent runs — their logs, their
-- cleanup receipts, their offloaded event payloads. None of that is expressed as a foreign key anywhere: the result row
-- carries digests and a JSON outcome, and the observations it was computed from name their run in benchmark_result.
-- So "is this log stream still cited by a sealed result" was a question no reaper could ask, and a reaper that cannot
-- ask it must never delete. The pin is the answer, written in the SAME transaction as the seal: if the seal commits,
-- every record it cites is pinned, and if it does not, no pin exists either.
--
-- WHY TWO TARGET COLUMNS. ArtifactReferenceOracle enumerates every column in the schema whose name ends in
-- `artifact_id` and whose target is workflow_artifact — that naming rule IS its completeness argument. An artifact pin
-- must therefore live in a column called pinned_artifact_id and nothing else may, or the oracle would read a log
-- stream id as an artifact reference. Every other pinned kind uses pinned_id. The CHECK ties the two together so the
-- pair can never disagree with the kind.
--
-- The uniqueness the pin needs is over (result, kind, target), which spans both nullable columns, so it is an
-- expression index rather than the primary key; the surrogate id keeps the row addressable like every other entity.
--
-- WHY retain_until AND purged_at. A log stream's bytes live in routed CAS objects that nothing ever reclaimed. The
-- reaper's two waits need somewhere durable to stand: retain_until is the instant the bytes become reclaimable — the
-- reaper writes it on the FIRST sweep that finds the stream terminal, past its rule and cited by nobody, and collects
-- only on a later sweep once it has passed. purged_at is the tombstone. The stream head row deliberately SURVIVES its
-- bytes: a reader that finds no row cannot tell "purged by policy" from "lost", and the whole point of a retention
-- plane is that reclamation is legible. Both are metadata-only nullable ADD COLUMNs (no rewrite, no index), and NULL
-- is the correct reading for every row an older binary writes or reads.
--
-- The rules that keep these two columns honest are in the guard function, not in a CHECK: see
-- 0236_agent_run_log_stream_retention.sql. A CHECK here would have to be validated against the whole table.
--
-- Rollback: DROP TABLE paired_qualification_result_pin; ALTER TABLE agent_run_log_stream DROP COLUMN retain_until,
-- DROP COLUMN purged_at. Nothing reads either from an older binary.

CREATE TABLE paired_qualification_result_pin (
    id uuid PRIMARY KEY,
    result_id uuid NOT NULL REFERENCES paired_qualification_result(observation_group_id) ON DELETE CASCADE,
    kind varchar(32) NOT NULL,
    pinned_id uuid NULL,
    pinned_artifact_id uuid NULL,
    pinned_at timestamptz NOT NULL,
    CONSTRAINT ck_paired_qualification_result_pin_kind
        CHECK (kind IN ('LogStream', 'Artifact', 'CleanupReceipt', 'AgentRun')),
    CONSTRAINT ck_paired_qualification_result_pin_target
        CHECK (num_nonnulls(pinned_id, pinned_artifact_id) = 1 AND (kind = 'Artifact') = (pinned_artifact_id IS NOT NULL))
);

CREATE UNIQUE INDEX ux_paired_qualification_result_pin
    ON paired_qualification_result_pin (result_id, kind, COALESCE(pinned_id, pinned_artifact_id));

-- One index per probed column, partial so each is only the rows that column addresses: every consumer asks
-- "EXISTS (SELECT 1 ... WHERE <column> = $1)", and `= $1` implies NOT NULL, so the partial index serves it.
CREATE INDEX ix_paired_qualification_result_pin_target
    ON paired_qualification_result_pin (pinned_id) WHERE pinned_id IS NOT NULL;
CREATE INDEX ix_paired_qualification_result_pin_artifact
    ON paired_qualification_result_pin (pinned_artifact_id) WHERE pinned_artifact_id IS NOT NULL;

COMMENT ON TABLE paired_qualification_result_pin IS
    'Which durable records a sealed paired-qualification result cites. Written in the seal''s own transaction; read by every retention cursor as the "still referenced" proof.';

-- Backfill, and it is not optional. A pin table that only fills going forward would make every result sealed before
-- this migration read as citing NOTHING, and the first reaper sweep would then find its evidence collectable. The four
-- statements below are the same four closures PairedQualificationResultStore writes for a new seal, in SQL: the runs a
-- result's observations name, and then that run's log streams, cleanup receipts and offloaded event payloads. They and
-- the writer are pinned against each other by an integration test.

INSERT INTO paired_qualification_result_pin (id, result_id, kind, pinned_id, pinned_artifact_id, pinned_at)
SELECT gen_random_uuid(), cited.result_id, 'AgentRun', cited.agent_run_id, NULL, now()
FROM (
    SELECT DISTINCT result.observation_group_id AS result_id, observation.agent_run_id
    FROM paired_qualification_result result
    JOIN benchmark_result observation ON observation.observation_group_id = result.observation_group_id
    WHERE observation.agent_run_id IS NOT NULL
) AS cited
ON CONFLICT DO NOTHING;

INSERT INTO paired_qualification_result_pin (id, result_id, kind, pinned_id, pinned_artifact_id, pinned_at)
SELECT gen_random_uuid(), cited.result_id, 'LogStream', cited.stream_id, NULL, now()
FROM (
    SELECT DISTINCT pin.result_id, stream.id AS stream_id
    FROM paired_qualification_result_pin pin
    JOIN agent_run_log_stream stream ON stream.agent_run_id = pin.pinned_id
    WHERE pin.kind = 'AgentRun'
) AS cited
ON CONFLICT DO NOTHING;

INSERT INTO paired_qualification_result_pin (id, result_id, kind, pinned_id, pinned_artifact_id, pinned_at)
SELECT gen_random_uuid(), cited.result_id, 'CleanupReceipt', cited.receipt_id, NULL, now()
FROM (
    SELECT DISTINCT pin.result_id, receipt.id AS receipt_id
    FROM paired_qualification_result_pin pin
    JOIN agent_run_cleanup_receipt receipt ON receipt.agent_run_id = pin.pinned_id
    WHERE pin.kind = 'AgentRun'
) AS cited
ON CONFLICT DO NOTHING;

INSERT INTO paired_qualification_result_pin (id, result_id, kind, pinned_id, pinned_artifact_id, pinned_at)
SELECT gen_random_uuid(), cited.result_id, 'Artifact', NULL, cited.data_artifact_id, now()
FROM (
    SELECT DISTINCT pin.result_id, event.data_artifact_id
    FROM paired_qualification_result_pin pin
    JOIN agent_run_event event ON event.agent_run_id = pin.pinned_id
    WHERE pin.kind = 'AgentRun' AND event.data_artifact_id IS NOT NULL
) AS cited
ON CONFLICT DO NOTHING;

ALTER TABLE agent_run_log_stream ADD COLUMN retain_until timestamptz NULL;
ALTER TABLE agent_run_log_stream ADD COLUMN purged_at timestamptz NULL;

COMMENT ON COLUMN agent_run_log_stream.retain_until IS
    'The earliest instant this stream''s bytes may be reclaimed, written on the first sweep that found it collectable. NULL means no sweep has ever proposed it.';
COMMENT ON COLUMN agent_run_log_stream.purged_at IS
    'When this stream''s segment bytes were reclaimed. The head row survives as the tombstone so a reader reads "purged", never a missing stream.';
