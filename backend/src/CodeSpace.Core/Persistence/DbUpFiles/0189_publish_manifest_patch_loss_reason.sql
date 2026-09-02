-- Deliverable-loss honesty: when a captured patch's bytes never reached the artifact store (offload refused or the
-- oversize inline copy was shed after a failed offload), the manifest row itself names the loss — so a reader (the
-- Room preview) can say WHY a listed file has no bytes instead of rendering a clickable chip that 404s. NULL means
-- "no loss": either the patch stored durably (patch_artifact_id set / inline present) or there was never a patch.
ALTER TABLE publish_manifest ADD COLUMN IF NOT EXISTS patch_loss_reason text NULL;
