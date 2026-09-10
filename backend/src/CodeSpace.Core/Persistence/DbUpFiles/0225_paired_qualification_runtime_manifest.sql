-- Freeze the RUNTIME BUNDLE beside the paired qualification protocol: the harness binary bytes actually executed,
-- the worker runtime, the gateway endpoint and salted secret identity behind each arm, who graded, and every
-- execution-affecting default. A suite digest, a model row id and a code revision do not pin any of it, so a
-- campaign could resume against a swapped CLI, a rotated key or a repointed gateway and pool two different
-- experiments into one claim.
--
-- Nullable, with the two columns constrained to appear together: legacy protocols committed before the bundle was
-- frozen stay readable and recoverable, exactly as 0224 did for the result digest.
--
-- NO SECRET MATERIAL may enter runtime_manifest_json. A credential contributes its provider, its host, its row id
-- and an HMAC of its key under the campaign salt — never the key. The application record makes that structural and
-- a serialization test pins it; this column simply stores what that record produced.
ALTER TABLE paired_qualification_protocol ADD COLUMN runtime_manifest_json jsonb NULL;
ALTER TABLE paired_qualification_protocol ADD COLUMN runtime_manifest_digest varchar(64) NULL;

-- Written NULL-SAFE deliberately. A CHECK passes on NULL, not only on TRUE, so the natural spelling
-- `(both null) OR (typeof = 'object' AND digest ~ ...)` admits a manifest with NO digest: the second branch
-- evaluates to NULL and NULL OR FALSE is NULL, which PostgreSQL accepts. Comparing the two NULL-nesses gives a
-- boolean that is never NULL, and each format clause then short-circuits on its own IS NULL.
ALTER TABLE paired_qualification_protocol ADD CONSTRAINT ck_paired_qualification_protocol_runtime_manifest
    CHECK ((runtime_manifest_json IS NULL) = (runtime_manifest_digest IS NULL)
        AND (runtime_manifest_json IS NULL OR jsonb_typeof(runtime_manifest_json) = 'object')
        AND (runtime_manifest_digest IS NULL OR runtime_manifest_digest ~ '^[0-9A-F]{64}$'));

COMMENT ON COLUMN paired_qualification_protocol.runtime_manifest_json IS 'The frozen QualificationRuntimeManifest as canonical JSON: harness binary sha256 per kind, runner profile, per-arm credential endpoint identity with a salted key fingerprint, reviewer resolution, execution settings. Never secret material.';
COMMENT ON COLUMN paired_qualification_protocol.runtime_manifest_digest IS 'SHA-256 of the frozen runtime manifest canonical bytes, folded into protocol_digest so a substituted runtime cannot reuse the campaign identity.';
