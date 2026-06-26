-- A per-credential "default model" marker: the model an "auto" run (no pinned model) prefers from the team pool,
-- so an operator can make "auto" use a model they know works (e.g. a gateway's metis-coder-max) instead of the
-- alphabetical-first fallback. At most one default per credential is enforced in the service (setting one clears
-- the others on that credential); the resolver orders is_default DESC first, so a marked model wins the pool pick.
ALTER TABLE model_credential_model ADD COLUMN is_default boolean NOT NULL DEFAULT false;
