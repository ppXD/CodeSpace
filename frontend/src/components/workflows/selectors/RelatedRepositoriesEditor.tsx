import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";

import { SearchSelect } from "./SearchSelect";

/**
 * agent.run "Add related repositories" editor (multi-repo PR5). Authors the ADDITIONAL repos an agent's
 * workspace clones alongside the primary (the `repositoryId` row above it) — the way to make a coordinated
 * change across e.g. a frontend + backend. Mirrors {@link TriggerRepositoriesSelector}'s repeatable
 * project→repo cascade rows, swapping the labels editor for an alias text input + a read/write access select.
 *
 * <h3>Wire shape</h3>
 * Emits `Array<{ repositoryId, alias?, access }>` into `inputs.relatedRepositories`, which the backend
 * AgentCodeNode folds (with the primary) into `AgentTask.Workspace`.
 *
 * <h3>Single-repo byte-identical</h3>
 * Empty list ⇒ emits `undefined` ⇒ the inspector spreads `relatedRepositories: undefined` ⇒ JSON.stringify
 * drops the key ⇒ the run is single-repo, byte-identical. (No match-all semantics — unlike the trigger picker,
 * an empty related list simply means "no extra repos".)
 */

export interface RelatedRepoEntry {
  repositoryId: string;
  alias?: string;
  access?: "read" | "write";
}

/** Tolerate a malformed / hand-edited shape: keep in-progress empty-id rows so the Add→Pick flow survives a re-render. */
function normaliseRelatedRepositories(value: unknown): RelatedRepoEntry[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((v) => {
    if (typeof v !== "object" || v === null) return [];
    const o = v as Record<string, unknown>;
    return [{
      repositoryId: typeof o.repositoryId === "string" ? o.repositoryId : "",
      alias: typeof o.alias === "string" && o.alias.trim() !== "" ? o.alias : undefined,
      access: o.access === "write" ? "write" : "read",
    }];
  });
}

interface RelatedRepositoriesEditorProps {
  value: unknown;
  onChange: (next: RelatedRepoEntry[] | undefined) => void;
}

export function RelatedRepositoriesEditor({ value, onChange }: RelatedRepositoriesEditorProps) {
  const entries = useMemo(() => normaliseRelatedRepositories(value), [value]);
  const projects = useProjects();
  const repositories = useRepositories();

  const projectRows = useMemo(() => projects.data ?? [], [projects.data]);
  const repoRows = useMemo(() => repositories.data ?? [], [repositories.data]);

  // Per-row "draft" project pick — UI narrowing only, never persisted (the saved row is { repositoryId, alias, access }).
  const [draftProjectByIndex, setDraftProjectByIndex] = useState<Map<number, string>>(new Map());

  const projectForRow = (idx: number, entry: RelatedRepoEntry): string => {
    const draft = draftProjectByIndex.get(idx);
    if (draft !== undefined) return draft;
    if (!entry.repositoryId) return "";
    return repoRows.find((r) => r.id === entry.repositoryId)?.projects?.[0]?.id ?? "";
  };

  // Empty ⇒ undefined ⇒ the key drops on save ⇒ single-repo byte-identical.
  const emit = (next: RelatedRepoEntry[]) => onChange(next.length === 0 ? undefined : next);

  const addRow = () => emit([...entries, { repositoryId: "", access: "read" }]);

  const removeRow = (idx: number) => {
    setDraftProjectByIndex((prev) => {
      const next = new Map<number, string>();
      for (const [k, v] of prev) {
        if (k < idx) next.set(k, v);
        else if (k > idx) next.set(k - 1, v);
      }
      return next;
    });
    emit(entries.filter((_, i) => i !== idx));
  };

  const updateRow = (idx: number, patch: Partial<RelatedRepoEntry>) =>
    emit(entries.map((entry, i) => (i === idx ? { ...entry, ...patch } : entry)));

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
    <div className="wf-relrepo" data-testid="related-repositories-editor">
      {entries.map((entry, idx) => (
          <RelatedRepoRow
            key={idx}
            entry={entry}
            projectId={projectForRow(idx, entry)}
            projects={projectRows}
            repositories={repoRows}
            onPickProject={(projectId) => pickProjectForRow(idx, projectId)}
            onPickRepo={(repositoryId) => updateRow(idx, { repositoryId })}
            onChangeAlias={(alias) => updateRow(idx, { alias: alias.trim() === "" ? undefined : alias })}
            onChangeAccess={(access) => updateRow(idx, { access })}
            onRemove={() => removeRow(idx)}
          />
        ))}

      <button type="button" className="wf-relrepo-add" onClick={addRow}>
        <Ic.Plus size={11} />
        <span>Add related repository</span>
      </button>

      {entries.length === 0 && (
        <div className="wf-relrepo-hint">
          <span aria-hidden="true">ⓘ</span>
          <span>No related repositories — a single-repo run.</span>
        </div>
      )}
    </div>
  );
}

interface RelatedRepoRowProps {
  entry: RelatedRepoEntry;
  projectId: string;
  projects: Array<{ id: string; name: string; slug: string }>;
  repositories: Array<{ id: string; fullPath: string; projects?: Array<{ id: string }> }>;
  onPickProject: (projectId: string) => void;
  onPickRepo: (repositoryId: string) => void;
  onChangeAlias: (alias: string) => void;
  onChangeAccess: (access: "read" | "write") => void;
  onRemove: () => void;
}

function RelatedRepoRow({
  entry,
  projectId,
  projects,
  repositories,
  onPickProject,
  onPickRepo,
  onChangeAlias,
  onChangeAccess,
  onRemove,
}: RelatedRepoRowProps) {
  const visibleRepos = useMemo(() => {
    if (!projectId) return repositories;
    return repositories.filter((r) => (r.projects ?? []).some((p) => p.id === projectId));
  }, [repositories, projectId]);

  return (
    <div className="wf-relrepo-row" data-testid="related-repositories-row">
      <button type="button" className="wf-relrepo-remove" onClick={onRemove} aria-label="Remove related repository" title="Remove">
        <Ic.X size={11} />
      </button>

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
          value={entry.repositoryId ? [entry.repositoryId] : []}
          onChange={(ids) => onPickRepo(ids[0] ?? "")}
          placeholder="Pick a repository…"
        />
      </div>

      <div className="wf-relrepo-field">
        <span className="wf-relrepo-flabel" title="The short name + mount folder for this repo (e.g. 'api')">Alias</span>
        <input
          type="text"
          className="wf-form-input"
          value={entry.alias ?? ""}
          onChange={(e) => onChangeAlias(e.target.value)}
          placeholder="auto (repo-2, repo-3, …)"
          aria-label="Alias"
        />
      </div>

      <div className="wf-relrepo-field">
        <span className="wf-relrepo-flabel">Access</span>
        <select className="wf-form-input" value={entry.access ?? "read"} onChange={(e) => onChangeAccess(e.target.value === "write" ? "write" : "read")} aria-label="Access">
          <option value="read">Read (context)</option>
          <option value="write">Write</option>
        </select>
      </div>
    </div>
  );
}
