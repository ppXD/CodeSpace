import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunListFilterInput } from "@/api/workflows";
import { useAgentDefinitions } from "@/hooks/use-agents";
import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";
import { useTeamMembers } from "@/hooks/use-team-members";

import { FilterSelect, type FilterOption } from "./FilterSelect";
import { BAR_DIMS } from "./cockpit";

/** Coarse origin kinds offered in the bar — plain-language labels over the run_kind wire tokens (child runs never reach
 *  the index). The `value` is the wire token the server filters by; only the display label is friendly. */
const KIND_OPTIONS: FilterOption[] = [
  { value: "workflow", label: "Automation" },
  { value: "task", label: "Task" },
  { value: "event", label: "Triggered" },
  { value: "replay", label: "Re-run" },
  { value: "schedule", label: "Scheduled" },
];

/**
 * The runs filter bar — the entity/scope lens over the team's runs (which kind · which repo · which project · who
 * launched · which agent). It writes the server-side {@link RunListFilterInput} the runs index fetches by; the status
 * cards above remain the orthogonal status/time lens, and the two AND together (e.g. Live card + Repository=X → live
 * runs scoped to repo X). Each dimension is MULTI-select: values within a facet are OR'd, facets AND together. Empty
 * bar = no scope, the full index.
 */
export function RunFilterBar({ filter, onChange }: { filter: RunListFilterInput; onChange: (next: RunListFilterInput) => void }) {
  const repos = useRepositories();
  const projects = useProjects();
  const members = useTeamMembers();
  const agents = useAgentDefinitions();

  const setMulti = (key: (typeof BAR_DIMS)[number], vs: string[]) => onChange({ ...filter, [key]: vs.length ? vs : undefined });

  const toOptions = <T,>(rows: readonly T[] | undefined, id: (r: T) => string, label: (r: T) => string): FilterOption[] =>
    (rows ?? []).map((r) => ({ value: id(r), label: label(r) }));

  const activeCount = BAR_DIMS.filter((d) => (filter[d]?.length ?? 0) > 0).length;
  const clearAll = () => onChange(Object.fromEntries(BAR_DIMS.map((d) => [d, undefined])) as RunListFilterInput);

  return (
    <div className="run-filterbar">
      <span className="run-filterbar-icon" aria-hidden="true"><Ic.Filter size={13} /></span>

      <FilterSelect label="Kind" options={KIND_OPTIONS} values={filter.runKinds ?? []} onChange={(vs) => setMulti("runKinds", vs)} />
      <FilterSelect label="Repository" options={toOptions(repos.data, (r) => r.id, (r) => r.name)} values={filter.repositoryIds ?? []} onChange={(vs) => setMulti("repositoryIds", vs)} loading={repos.isLoading} />
      <FilterSelect label="Project" options={toOptions(projects.data, (p) => p.id, (p) => p.name)} values={filter.projectIds ?? []} onChange={(vs) => setMulti("projectIds", vs)} loading={projects.isLoading} />
      <FilterSelect label="Launched by" options={toOptions(members.data, (m) => m.userId, (m) => m.name)} values={filter.actorIds ?? []} onChange={(vs) => setMulti("actorIds", vs)} loading={members.isLoading} />
      <FilterSelect label="Agent" options={toOptions(agents.data, (a) => a.id, (a) => a.name)} values={filter.agentDefinitionIds ?? []} onChange={(vs) => setMulti("agentDefinitionIds", vs)} loading={agents.isLoading} />

      {activeCount > 0 && (
        <button type="button" className="run-filterbar-clear" onClick={clearAll}>
          <Ic.X size={12} aria-hidden="true" /> Clear{activeCount > 1 ? ` (${activeCount})` : ""}
        </button>
      )}
    </div>
  );
}
