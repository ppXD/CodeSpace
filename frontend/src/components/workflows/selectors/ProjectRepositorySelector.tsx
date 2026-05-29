import { useMemo, useState } from "react";

import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";

interface ProjectRepositorySelectorProps {
  /** Selected repository UUID ("" = none chosen yet). */
  value: string;
  onChange: (next: string) => void;
}

/**
 * Single-repository picker with a project→repo cascade, mirroring the trigger's repo row: the
 * Project dropdown only NARROWS the repository list — it isn't stored. The saved value is the
 * chosen repository's UUID, which flows downstream as a plain `{{input.<name>}}` string.
 *
 * Used by the schema-driven form whenever a field declares `"x-selector": "repository"` — so a
 * manual workflow can let the operator pick a repo at run time and pass its id to later nodes.
 */
export function ProjectRepositorySelector({ value, onChange }: ProjectRepositorySelectorProps) {
  const projects = useProjects();
  const repositories = useRepositories();

  const projectRows = useMemo(() => projects.data ?? [], [projects.data]);
  const repoRows = useMemo(() => repositories.data ?? [], [repositories.data]);

  // Project is a UI narrowing aid only (never persisted) — default to "All projects".
  const [project, setProject] = useState("");

  const visibleRepos = useMemo(() => {
    if (!project) return repoRows;
    return repoRows.filter((r) => (r.projects ?? []).some((p) => p.id === project));
  }, [repoRows, project]);

  return (
    <div className="wf-repo-pick">
      <label className="wf-repo-pick-field">
        <span className="wf-repo-pick-label">Project</span>
        <select
          className="wf-form-input"
          value={project}
          onChange={(e) => setProject(e.target.value)}
          aria-label="Project"
        >
          <option value="">All projects</option>
          {projectRows.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      </label>

      <label className="wf-repo-pick-field">
        <span className="wf-repo-pick-label">Repository</span>
        <select
          className="wf-form-input"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          aria-label="Repository"
        >
          <option value="">{repositories.isLoading ? "Loading…" : "Pick a repository…"}</option>
          {visibleRepos.map((r) => <option key={r.id} value={r.id}>{r.fullPath}</option>)}
        </select>
      </label>
    </div>
  );
}
