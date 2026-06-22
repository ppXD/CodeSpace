import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunListFilterInput } from "@/api/workflows";
import { useAgentDefinitions } from "@/hooks/use-agents";
import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";
import { useTeamMembers } from "@/hooks/use-team-members";

import { FilterSelect, type FilterOption } from "./FilterSelect";

/** Coarse origin kinds offered in the bar — the user-meaningful run_kind tokens (child runs never reach the index). */
const KIND_OPTIONS: FilterOption[] = [
  { value: "workflow", label: "Workflow" },
  { value: "task", label: "Task" },
  { value: "event", label: "Event" },
  { value: "replay", label: "Replay" },
  { value: "schedule", label: "Schedule" },
];

/** The entity/scope dimensions the bar controls (the cards own the status/time lens) — used for the active-count + Clear. */
const BAR_DIMS = ["runKinds", "repositoryIds", "projectIds", "actorIds", "agentDefinitionIds"] as const;

/**
 * The runs filter bar — the entity/scope lens over the team's runs (which kind · which repo · which project · who
 * launched · which agent). It writes the server-side {@link RunListFilterInput} the runs index fetches by; the status
 * cards above remain the orthogonal status/time lens, and the two AND together (e.g. Live card + Repository=X → live
 * runs scoped to repo X). Each dimension is single-select for v1 (one value → a one-element list on the wire); the
 * backend takes a list, so widening to multi-select later is additive. Empty bar = no scope, the full index.
 */
export function RunFilterBar({ filter, onChange }: { filter: RunListFilterInput; onChange: (next: RunListFilterInput) => void }) {
  const repos = useRepositories();
  const projects = useProjects();
  const members = useTeamMembers();
  const agents = useAgentDefinitions();

  const first = (xs?: string[]) => xs?.[0] ?? null;
  const set = (key: (typeof BAR_DIMS)[number], v: string | null) => onChange({ ...filter, [key]: v ? [v] : undefined });

  const toOptions = <T,>(rows: readonly T[] | undefined, id: (r: T) => string, label: (r: T) => string): FilterOption[] =>
    (rows ?? []).map((r) => ({ value: id(r), label: label(r) }));

  const activeCount = BAR_DIMS.filter((d) => (filter[d]?.length ?? 0) > 0).length;
  const clearAll = () => onChange(Object.fromEntries(BAR_DIMS.map((d) => [d, undefined])) as RunListFilterInput);

  return (
    <div className="run-filterbar">
      <span className="run-filterbar-icon" aria-hidden="true"><Ic.Filter size={13} /></span>

      <FilterSelect label="Kind" options={KIND_OPTIONS} value={first(filter.runKinds)} onChange={(v) => set("runKinds", v)} />
      <FilterSelect label="Repository" options={toOptions(repos.data, (r) => r.id, (r) => r.name)} value={first(filter.repositoryIds)} onChange={(v) => set("repositoryIds", v)} loading={repos.isLoading} />
      <FilterSelect label="Project" options={toOptions(projects.data, (p) => p.id, (p) => p.name)} value={first(filter.projectIds)} onChange={(v) => set("projectIds", v)} loading={projects.isLoading} />
      <FilterSelect label="Launched by" options={toOptions(members.data, (m) => m.userId, (m) => m.name)} value={first(filter.actorIds)} onChange={(v) => set("actorIds", v)} loading={members.isLoading} />
      <FilterSelect label="Agent" options={toOptions(agents.data, (a) => a.id, (a) => a.name)} value={first(filter.agentDefinitionIds)} onChange={(v) => set("agentDefinitionIds", v)} loading={agents.isLoading} />

      {activeCount > 0 && (
        <button type="button" className="run-filterbar-clear" onClick={clearAll}>
          Clear{activeCount > 1 ? ` (${activeCount})` : ""}
        </button>
      )}
    </div>
  );
}
