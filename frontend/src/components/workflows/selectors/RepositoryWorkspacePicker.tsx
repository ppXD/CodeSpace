import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";
import {
  readWorkspaceRepos,
  writeWorkspaceRepos,
  type WorkspaceRepoRow,
  type WorkspaceReposEmit,
} from "@/lib/nodeRepoWorkspace";

import { SearchSelect } from "./SearchSelect";

/**
 * The unified repository-workspace picker for the agent.run / agent.supervisor nodes — ONE flat list where
 * row 0 is the PRIMARY (the writable workspace root) and every following row is a related repo the agent also
 * clones for a coordinated multi-repo change. Mirrors the ad-hoc Launch composer's model so both surfaces read
 * the same. It round-trips through {@link readWorkspaceRepos}/{@link writeWorkspaceRepos} to the engine's existing
 * `{ repositoryId, relatedRepositories }` shape — the on-disk config is byte-identical.
 *
 * The primary row shows a "workspace root · writable" marker instead of alias/access controls: the fold hardcodes
 * the primary to the root alias + write, and whether the agent may actually write is the run-level Autonomy
 * setting — so those controls would be dead on the primary. Related rows carry a real alias + access.
 */

interface RepositoryWorkspacePickerProps {
  /** Persisted scalar primary repo id ("" = none chosen yet). */
  repositoryId: string;
  /** Persisted `relatedRepositories` array (tolerated unknown — normalised on read). */
  relatedRepositories: unknown;
  onChange: (next: WorkspaceReposEmit) => void;
}

export function RepositoryWorkspacePicker({ repositoryId, relatedRepositories, onChange }: RepositoryWorkspacePickerProps) {
  const rows = useMemo(() => readWorkspaceRepos(repositoryId, relatedRepositories), [repositoryId, relatedRepositories]);
  const projects = useProjects();
  const repositories = useRepositories();

  const projectRows = useMemo(() => projects.data ?? [], [projects.data]);
  const repoRows = useMemo(() => repositories.data ?? [], [repositories.data]);

  // Per-row project narrowing — UI aid only, never persisted. Keyed by row index; reset on reorder.
  const [draftProjectByIndex, setDraftProjectByIndex] = useState<Map<number, string>>(new Map());

  const projectForRow = (idx: number, row: WorkspaceRepoRow): string => {
    const draft = draftProjectByIndex.get(idx);
    if (draft !== undefined) return draft;
    if (!row.repositoryId) return "";
    return repoRows.find((r) => r.id === row.repositoryId)?.projects?.[0]?.id ?? "";
  };

  const emit = (next: WorkspaceRepoRow[]) => onChange(writeWorkspaceRepos(next));

  const addRow = () => emit([...rows, { repositoryId: "", alias: "", access: "read" }]);

  const removeRow = (idx: number) => {
    setDraftProjectByIndex((prev) => {
      const next = new Map<number, string>();
      for (const [k, v] of prev) {
        if (k < idx) next.set(k, v);
        else if (k > idx) next.set(k - 1, v);
      }
      return next;
    });
    emit(rows.filter((_, i) => i !== idx));
  };

  const updateRow = (idx: number, patch: Partial<WorkspaceRepoRow>) =>
    emit(rows.map((row, i) => (i === idx ? { ...row, ...patch } : row)));

  // Promote a related row to the primary slot (index 0). Indices reshuffle, so drop the narrowing drafts.
  const makePrimary = (idx: number) => {
    if (idx === 0) return;
    setDraftProjectByIndex(new Map());
    const chosen = rows[idx]!;
    emit([chosen, ...rows.filter((_, i) => i !== idx)]);
  };

  const pickProjectForRow = (idx: number, projectId: string) => {
    setDraftProjectByIndex((prev) => {
      const next = new Map(prev);
      if (projectId === "") next.delete(idx);
      else next.set(idx, projectId);
      return next;
    });
    updateRow(idx, { repositoryId: "" });
  };

  return (
    <div className="wf-relrepo" data-testid="repository-workspace-picker">
      {rows.map((row, idx) => (
        <RepoRow
          key={idx}
          row={row}
          isPrimary={idx === 0}
          projectId={projectForRow(idx, row)}
          projects={projectRows}
          repositories={repoRows}
          onPickProject={(projectId) => pickProjectForRow(idx, projectId)}
          onPickRepo={(id) => updateRow(idx, { repositoryId: id })}
          onChangeAlias={(alias) => updateRow(idx, { alias })}
          onChangeAccess={(access) => updateRow(idx, { access })}
          onMakePrimary={() => makePrimary(idx)}
          onRemove={() => removeRow(idx)}
        />
      ))}

      <button type="button" className="wf-relrepo-add" onClick={addRow}>
        <Ic.Plus size={11} />
        <span>{rows.length === 0 ? "Add a repository" : "Add another repository"}</span>
      </button>

      {rows.length === 0 && (
        <div className="wf-relrepo-hint">
          <span aria-hidden="true">ⓘ</span>
          <span>No repository — an analysis-only run.</span>
        </div>
      )}
    </div>
  );
}

interface RepoRowProps {
  row: WorkspaceRepoRow;
  isPrimary: boolean;
  projectId: string;
  projects: Array<{ id: string; name: string }>;
  repositories: Array<{ id: string; fullPath: string; projects?: Array<{ id: string }> }>;
  onPickProject: (projectId: string) => void;
  onPickRepo: (repositoryId: string) => void;
  onChangeAlias: (alias: string) => void;
  onChangeAccess: (access: "read" | "write") => void;
  onMakePrimary: () => void;
  onRemove: () => void;
}

function RepoRow({
  row,
  isPrimary,
  projectId,
  projects,
  repositories,
  onPickProject,
  onPickRepo,
  onChangeAlias,
  onChangeAccess,
  onMakePrimary,
  onRemove,
}: RepoRowProps) {
  const visibleRepos = useMemo(() => {
    if (!projectId) return repositories;
    return repositories.filter((r) => (r.projects ?? []).some((p) => p.id === projectId));
  }, [repositories, projectId]);

  return (
    <div
      className={`wf-relrepo-row wf-wsrepo-row${isPrimary ? " wf-wsrepo-row-primary" : ""}`}
      data-testid={isPrimary ? "workspace-primary-row" : "workspace-related-row"}
    >
      <div className="wf-relrepo-rowhead">
        <span className={`wf-relrepo-badge${isPrimary ? " wf-relrepo-badge-primary" : ""}`}>{isPrimary ? "Primary" : "Related"}</span>

        <div className="wf-relrepo-rowactions">
          {!isPrimary && (
            <button type="button" className="wf-relrepo-makeprimary" onClick={onMakePrimary} title="Make this the primary repository">
              <Ic.Star size={10} />
              <span>Make primary</span>
            </button>
          )}
          <button type="button" className="wf-relrepo-remove-inline" onClick={onRemove} aria-label="Remove repository" title="Remove">
            <Ic.X size={11} />
          </button>
        </div>
      </div>

      <div className="wf-relrepo-field">
        <span className="wf-relrepo-flabel">Project</span>
        <SearchSelect
          options={projects.map((p) => ({ id: p.id, label: p.name }))}
          value={projectId ? [projectId] : []}
          onChange={(ids) => onPickProject(ids[0] ?? "")}
          placeholder="All projects"
        />
      </div>

      <div className="wf-relrepo-field">
        <span className="wf-relrepo-flabel">Repository</span>
        <SearchSelect
          options={visibleRepos.map((r) => ({ id: r.id, label: r.fullPath }))}
          value={row.repositoryId ? [row.repositoryId] : []}
          onChange={(ids) => onPickRepo(ids[0] ?? "")}
          placeholder="Pick a repository…"
        />
      </div>

      {isPrimary ? (
        <div className="wf-relrepo-primarymark">
          <Ic.Folder size={11} />
          <span>Workspace root · <strong>writable</strong></span>
          <span className="wf-relrepo-primaryhint">read/write is set by Autonomy below</span>
        </div>
      ) : (
        <>
          <div className="wf-relrepo-field">
            <span className="wf-relrepo-flabel" title="The short name + mount folder for this repo (e.g. 'api')">Alias</span>
            <input
              type="text"
              className="wf-form-input"
              value={row.alias}
              onChange={(e) => onChangeAlias(e.target.value)}
              placeholder="auto (repo-2, repo-3, …)"
              aria-label="Alias"
            />
          </div>

          <div className="wf-relrepo-field">
            <span className="wf-relrepo-flabel">Access</span>
            <select
              className="wf-form-input"
              value={row.access}
              onChange={(e) => onChangeAccess(e.target.value === "write" ? "write" : "read")}
              aria-label="Access"
            >
              <option value="read">Read (context)</option>
              <option value="write">Write</option>
            </select>
          </div>
        </>
      )}
    </div>
  );
}
