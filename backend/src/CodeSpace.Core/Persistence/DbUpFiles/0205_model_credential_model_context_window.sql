-- Model context capacity belongs to the exact credentialed-model row: an opaque or reused gateway alias cannot be
-- classified safely from its name. NULL preserves the provider's authoritative overflow fallback. Positive values
-- let long-running callers compact before knowingly sending an over-capacity request.
ALTER TABLE model_credential_model ADD COLUMN IF NOT EXISTS context_window_tokens integer NULL;

ALTER TABLE model_credential_model DROP CONSTRAINT IF EXISTS ck_model_credential_model_context_window_tokens;
ALTER TABLE model_credential_model ADD CONSTRAINT ck_model_credential_model_context_window_tokens
    CHECK (context_window_tokens IS NULL OR context_window_tokens > 0);
