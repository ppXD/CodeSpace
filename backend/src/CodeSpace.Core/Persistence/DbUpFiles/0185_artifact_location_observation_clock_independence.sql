-- 0185_artifact_location_observation_clock_independence.sql
--
-- verified_at records the verifier worker's honest wall-clock reading. created_date records the writer's, and the two
-- workers need not share a clock: the normal CAS writer currently stamps creation from Postgres, while the later
-- verifier deliberately accepts a TimeProvider from its own process. A verifier only slightly behind the writer could
-- therefore prove the bytes present and then have that observation refused by `verified_at >= created_date`. The row
-- stayed Available but its revision and cursor never advanced, so a small deployment selected and silently lost the
-- same answer every hour.
--
-- Causality is already structural: a location is inserted at revision 1, every later observation advances exactly one
-- revision, and the deferred guard requires its byte-identical append-only event. Comparing two machines' timestamps
-- adds no proof. Keep every intrinsic observation invariant — non-negative size and the complete exact-Sha256 shape
-- required of Available — and remove only that cross-clock comparison.
--
-- NOT VALID IS DELIBERATE AND SAFE. DbUp runs the entire upgrade in one transaction. Re-validating this weaker check
-- would scan all artifact_location rows while the ALTER TABLE lock remains held until that whole upgrade commits. The
-- constraint being replaced was validated and strictly implies this one, so it already proved every existing row.
-- PostgreSQL enforces a NOT VALID CHECK for every later INSERT and UPDATE; skipping the redundant historical scan
-- changes only the catalog's convalidated flag, which no planner path in this application consumes.
--
-- One ALTER keeps the replacement atomic: no concurrent writer can enter between dropping the stronger predecessor
-- and installing this weaker successor.

ALTER TABLE artifact_location
    DROP CONSTRAINT ck_artifact_location_observation,
    ADD CONSTRAINT ck_artifact_location_observation CHECK (
        (observed_size_bytes IS NULL OR observed_size_bytes >= 0)
        AND (state <> 'Available' OR (verified_at IS NOT NULL AND observed_size_bytes IS NOT NULL
            AND provider_checksum_algorithm = 'Sha256' AND provider_checksum IS NOT NULL
            AND octet_length(provider_checksum) = 32 AND last_error_code IS NULL))) NOT VALID;

COMMENT ON CONSTRAINT ck_artifact_location_observation ON artifact_location IS
    'Intrinsic observation facts only. NOT VALID avoids a redundant table scan: its validated predecessor strictly implied this weaker check; every later INSERT/UPDATE is enforced.';
