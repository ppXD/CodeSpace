-- 0247_sealed_agent_task_and_child_run_payload.sql
--
-- Two public columns held resolved workflow secrets in plaintext because they are also what the platform executes:
--
--   * agent_run.task_jsonb — an agent.run goal that references {{team.X}} / {{wf.X}} / {{project.p.X}} is resolved
--     before staging, and the envelope was persisted whole. Viewers read it back as GetAgentRunQuery.Goal.
--   * workflow_run_request.normalized_payload_json — a flow.subworkflow child's inputs, resolved in the parent, are
--     the child's trigger payload. Viewers read it back as the child run's NormalizedPayload, and rerun copied it.
--
-- Both columns now hold a MASKED copy (every secret value the staging scope resolved replaced with [REDACTED]), and
-- the exact copy plus the list of secret values it embeds live in the encrypted column added here. The executor and
-- the child's scope build open that copy; every other reader keeps reading the masked column. The secret values ride
-- with the exact copy so the agent run's redactor and the child run's leak guard keep treating them as secrets.
--
-- NULLABLE with NO DEFAULT on purpose: a catalog-only ADD COLUMN on two hot tables, and NULL is exactly "nothing was
-- masked" — every row written before this migration, and every row whose payload carries no secret, keeps executing
-- from its public column unchanged.

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS sealed_task_ciphertext TEXT NULL;

COMMENT ON COLUMN agent_run.sealed_task_ciphertext IS
    'Data Protection ciphertext of the exact AgentTask envelope and the workflow secret values it embeds. Set only when task_jsonb had a secret masked; the executor launches from it. Never returned by any read API.';

ALTER TABLE workflow_run_request ADD COLUMN IF NOT EXISTS sealed_payload_ciphertext TEXT NULL;

COMMENT ON COLUMN workflow_run_request.sealed_payload_ciphertext IS
    'Data Protection ciphertext of the exact normalized payload and the inherited secret values it embeds (a flow.subworkflow child). Set only when normalized_payload_json had a secret masked; the engine builds the run scope from it. Never returned by any read API.';
