import { useMemo, useState } from "react";

import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";

import { SearchSelect, type SearchOption } from "./SearchSelect";

interface ProjectRepositorySelectorProps {
  /** Selected repository UUID ("" = none chosen yet). */
  value: string;
  onChange: (next: string) => void;
}

/**
 * Single-repository picker with a project→repo cascade (`"x-selector": "repository"`). The Project box only
 * NARROWS the repository list — it isn't stored; the saved value is the chosen repository's UUID. Both boxes
 * render the shared {@link SearchSelect} combobox so they match every other dropdown.
 */
export function ProjectRepositorySelector({ value, onChange }: ProjectRepositorySelectorProps) {
  const projects = useProjects();
  const repositories = useRepositories();

  const repoRows = useMemo(() => repositories.data ?? [], [repositories.data]);

  // Project is a UI narrowing aid only (never persisted) — default to "All projects".
  const [project, setProject] = useState("");

  const visibleRepos = useMemo(
    () => (project ? repoRows.filter((r) => (r.projects ?? []).some((p) => p.id === project)) : repoRows),
    [repoRows, project],
  );

  const projectOptions: SearchOption[] = (projects.data ?? []).map((p) => ({ id: p.id, label: p.name }));
  const repoOptions: SearchOption[] = visibleRepos.map((r) => ({ id: r.id, label: r.fullPath }));

  return (
    <div className="wf-repo-pick">
      <label className="wf-repo-pick-field">
        <span className="wf-repo-pick-label">Project</span>
        <SearchSelect
          options={projectOptions}
          value={project ? [project] : []}
          onChange={(ids) => setProject(ids[0] ?? "")}
          loading={projects.isLoading}
          placeholder="All projects"
        />
      </label>

      <label className="wf-repo-pick-field">
        <span className="wf-repo-pick-label">Repository</span>
        <SearchSelect
          options={repoOptions}
          value={value ? [value] : []}
          onChange={(ids) => onChange(ids[0] ?? "")}
          loading={repositories.isLoading}
          placeholder="Pick a repository…"
        />
      </label>
    </div>
  );
}
