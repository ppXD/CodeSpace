import type { AgentDefinitionSummary } from "@/api/agents";
import { useAgentDefinitions } from "@/hooks/use-agents";

interface AgentSelectorProps {
  /** Selected agent-persona UUID ("" = none chosen yet). */
  value: string;
  onChange: (next: string) => void;
}

/**
 * Single-persona picker. Lists the team's Agent personas; the saved value is the chosen persona's
 * UUID, which the `agent.code` node carries as `agentDefinitionId` and the dispatch-time resolver
 * merges into the run.
 *
 * Used by the schema-driven form whenever a field declares `"x-selector": "agent"` — generic, not
 * tied to any one node.
 */
export function AgentSelector({ value, onChange }: AgentSelectorProps) {
  const agents = useAgentDefinitions();
  const rows = agents.data ?? [];

  return (
    <select
      className="wf-form-input"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label="Agent persona"
    >
      <option value="">{agents.isLoading ? "Loading…" : "Pick an agent…"}</option>
      {rows.map((a) => <option key={a.id} value={a.id}>{agentLabel(a)}</option>)}
    </select>
  );
}

/** Persona name, with the @-handle as a disambiguating hint; falls back to the handle if unnamed. */
function agentLabel(a: AgentDefinitionSummary): string {
  return a.name ? `${a.name} (@${a.slug})` : `@${a.slug}`;
}
