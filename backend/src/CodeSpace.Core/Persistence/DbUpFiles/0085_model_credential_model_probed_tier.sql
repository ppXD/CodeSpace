-- An OBJECTIVELY-PROBED capability tier for OPAQUE model ids — the hook the capability_tier (0081) comment named. The
-- brain tiers known ids from the id alone; an opaque / renamed gateway alias it can't recognise lands capability_tier =
-- 'Unknown'. A recurring probe then has THAT model DEMONSTRATE capability on a fixed in-code micro-battery (deterministic
-- known-answer coding + structured-output tasks, graded in code — never self-rated) and records a COARSE tier here.
--
-- Kept SEPARATE from capability_tier ON PURPOSE: the brain-vs-probe provenance stays legible (capability_tier='Unknown'
-- still means "the brain didn't recognise the id"), and the two producers never fight over a row — tiering fires on
-- capability_tier IS NULL, the probe fires on capability_tier = 'Unknown'. The probe caps at 'Strong' and NEVER writes
-- 'Frontier' (a small battery can't honestly separate Strong from Frontier; under-ranking is the safe error), and only
-- ever UPGRADES (monotonic — a later flaky run never downgrades a good verdict). last_probed_capability_at gates
-- re-probing (a days-long window — an opaque alias's backing model rarely changes).
--
-- Like capability_tier this is an ADVISORY ORDERING hint, never a selection gate: the auto pick reads the EFFECTIVE tier
-- = COALESCE(probed_capability_tier, capability_tier) so a probed 'Strong' lifts a capable opaque model off last place
-- without erasing the brain verdict. NULL on both = un-probed = orders as Unknown, byte-identical to before this column.
ALTER TABLE model_credential_model ADD COLUMN probed_capability_tier text NULL;
ALTER TABLE model_credential_model ADD COLUMN last_probed_capability_at timestamptz NULL;
