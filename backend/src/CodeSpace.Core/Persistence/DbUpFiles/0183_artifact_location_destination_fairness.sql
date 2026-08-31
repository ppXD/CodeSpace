-- Gives the deployment-wide verification sweep an index it can actually use.
--
-- The sweep is not tenant-scoped on purpose: bytes rot per destination, not per team, and a retired profile still
-- serves its own. So its two ordering queries filter on state alone. The only index that looked like theirs,
-- ix_artifact_location_state_verified (0127:95-96), leads on team_id — which none of them supply — so Postgres could
-- not use it at all and both shares of every hourly batch were a full sequential scan of every placement in the
-- deployment plus a Top-N sort. That is the cost that grows with the table rather than with the batch.
--
-- state leads because it is the only equality qual. team_id and storage_profile_revision_id follow in that order
-- because together they are the destination pin the sweep now ranks within, and verified_at then id complete the
-- order each destination's rows are taken in — so the whole ranking arrives pre-sorted and needs no Sort node of its
-- own. Every column the ordering reads is in the index, which keeps it an index-only scan.
--
-- Additive: a CREATE INDEX changes no row and no constraint, and the existing index is left in place because the
-- per-team readers (a team's own placement list) still lead on team_id and are served by it.
--
-- Rollback: DROP INDEX ix_artifact_location_state_destination_verified.

CREATE INDEX ix_artifact_location_state_destination_verified
    ON artifact_location (state, team_id, storage_profile_revision_id, verified_at, id);
