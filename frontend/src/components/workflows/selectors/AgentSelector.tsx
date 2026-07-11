import type { AgentDefinitionSummary } from "@/api/agents";
import { useAgentDefinitions } from "@/hooks/use-agents";

import { SearchSelect, type SearchOption } from "./SearchSelect";

/**
 * Persona pickers for a `"x-selector": "agent"` field — the saved value is the persona's UUID (single) or an
 * array of them (multi, e.g. the supervisor's allowedAgentDefinitionIds). Both render the shared
 * {@link SearchSelect} combobox so agent dropdowns match the model / repo ones.
 */
function toOption(a: AgentDefinitionSummary): SearchOption {
  return { id: a.id, label: a.name || `@${a.slug}`, meta: a.name ? `@${a.slug}` : undefined };
}

/** Single-persona picker. */
export function AgentSelector({ value, onChange }: { value: string; onChange: (next: string) => void }) {
  const agents = useAgentDefinitions();
  const options = (agents.data ?? []).map(toOption);

  return (
    <SearchSelect
      options={options}
      value={value ? [value] : []}
      onChange={(ids) => onChange(ids[0] ?? "")}
      loading={agents.isLoading}
      placeholder="Pick an agent…"
    />
  );
}

/** Multi-persona picker. Value = an array of persona UUIDs. Empty = the whole team pool is allowed. */
export function AgentMultiSelector({ value, onChange }: { value: string[]; onChange: (next: string[]) => void }) {
  const agents = useAgentDefinitions();
  const options = (agents.data ?? []).map(toOption);

  return (
    <SearchSelect
      multi
      options={options}
      value={value}
      onChange={onChange}
      loading={agents.isLoading}
      placeholder="Search agents…"
      hint={value.length === 0 ? "None selected — any of the team's personas may be used." : `${value.length} selected — dispatched agents must use one of these.`}
    />
  );
}
