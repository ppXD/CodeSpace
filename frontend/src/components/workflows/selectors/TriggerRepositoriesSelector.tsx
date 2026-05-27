import { useMemo } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";
import {
  migrateLegacyTriggerConfig,
  type TriggerConfigArrayShape,
  type TriggerRepoEntry,
} from "@/lib/migrateTriggerConfig";

/**
 * Trigger-inspector picker for the
 * <c>{ repositories: [{ repositoryId, labels? }] }</c> config shape (see PR #23
 * for the matcher side). Renders a list editor:
 *
 *   ┌─ row ────────────────────────────────────────────────────────┐
 *   │  [ Project ▾ ]   [ Repo ▾ ]   [ label-1  label-2  + ]   [✕] │
 *   └──────────────────────────────────────────────────────────────┘
 *
 * The project dropdown narrows the repo dropdown (cascade) — picking
 * <c>frontend-platform</c> shows only repos linked to that project. This is
 * UX-only; the saved shape is still <c>{ repositoryId, labels }</c> per row,
 * no project-id stored. The matcher dispatches on repositoryId.
 *
 * <h3>Auto-migration</h3>
 * The incoming <c>value</c> may be the legacy <c>{ repositoryId, labels? }</c>
 * shape (configs saved before PR #23). The selector normalises through
 * <see cref="migrateLegacyTriggerConfig"/> before render; the first
 * <c>onChange</c> emits the new array shape, so the storage row auto-migrates
 * on first save. No offline data migration job needed.
 */

interface TriggerRepositoriesSelectorProps {
  value: unknown;
  onChange: (next: TriggerConfigArrayShape) => void;
}

export function TriggerRepositoriesSelector({ value, onChange }: TriggerRepositoriesSelectorProps) {
  const shape = useMemo(() => migrateLegacyTriggerConfig(value), [value]);
  const projects = useProjects();
  const repositories = useRepositories();

  const projectRows = useMemo(() => projects.data ?? [], [projects.data]);
  const repoRows = useMemo(() => repositories.data ?? [], [repositories.data]);

  // Emit the raw shape (including in-progress empty-repositoryId rows) so the picker
  // can survive a re-render with the row the user just added. The matcher tolerates
  // empty entries (PrTriggerMatcherFilter.EntryMatches skips them), and a parent that
  // wants a clean wire format can call normaliseTriggerConfigForSave at save time.
  const emit = (next: TriggerRepoEntry[]) => onChange({ repositories: next });

  const addRow = () => emit([...shape.repositories, { repositoryId: "" }]);
  const removeRow = (idx: number) => emit(shape.repositories.filter((_, i) => i !== idx));

  const updateRow = (idx: number, patch: Partial<TriggerRepoEntry>) => {
    const merged = shape.repositories.map((entry, i) => (i === idx ? { ...entry, ...patch } : entry));
    emit(merged);
  };

  return (
    <div className="wf-trigger-repos" data-testid="trigger-repositories-selector">
      {shape.repositories.length === 0 && (
        <div className="wf-trigger-repos-empty">
          No repositories selected — the trigger fires on every repo bound to this team. Add a row to scope it.
        </div>
      )}

      {shape.repositories.map((entry, idx) => (
        <TriggerRepoRow
          key={idx}
          entry={entry}
          projects={projectRows}
          repositories={repoRows}
          onPickProject={(projectId) => updateRow(idx, { repositoryId: "", labels: filterLabelsForProjectChange(entry.labels) })}
          onPickRepo={(repositoryId) => updateRow(idx, { repositoryId })}
          onChangeLabels={(labels) => updateRow(idx, { labels })}
          onRemove={() => removeRow(idx)}
        />
      ))}

      <button type="button" className="wf-trigger-repos-add" onClick={addRow}>
        <Ic.Plus size={11} />
        <span>Add repository</span>
      </button>
    </div>
  );
}

/**
 * Picking a new project clears the repo on the row — the previous repo almost
 * certainly belongs to the old project. Labels carry over unchanged because the
 * operator's intent ("PRs labeled X") is provider-agnostic.
 */
function filterLabelsForProjectChange(labels: string[] | undefined): string[] | undefined {
  return labels;
}

interface TriggerRepoRowProps {
  entry: TriggerRepoEntry;
  projects: Array<{ id: string; name: string; slug: string }>;
  repositories: Array<{ id: string; fullPath: string; projects?: Array<{ id: string }> }>;
  onPickProject: (projectId: string) => void;
  onPickRepo: (repositoryId: string) => void;
  onChangeLabels: (labels: string[]) => void;
  onRemove: () => void;
}

function TriggerRepoRow({
  entry,
  projects,
  repositories,
  onPickProject,
  onPickRepo,
  onChangeLabels,
  onRemove,
}: TriggerRepoRowProps) {
  // Infer the row's project from the picked repo so a saved config (which only
  // stores repositoryId) round-trips correctly through the cascade picker. If no
  // repo is picked yet, the project dropdown sits at "All projects".
  const inferredProjectId = useMemo(() => {
    if (!entry.repositoryId) return "";
    const repo = repositories.find((r) => r.id === entry.repositoryId);
    return repo?.projects?.[0]?.id ?? "";
  }, [entry.repositoryId, repositories]);

  const projectId = inferredProjectId;

  const visibleRepos = useMemo(() => {
    if (!projectId) return repositories;
    return repositories.filter((r) => (r.projects ?? []).some((p) => p.id === projectId));
  }, [repositories, projectId]);

  return (
    <div className="wf-trigger-repos-row" data-testid="trigger-repositories-row">
      <select
        className="wf-trigger-repos-project"
        value={projectId}
        onChange={(e) => onPickProject(e.target.value)}
        aria-label="Project"
      >
        <option value="">All projects</option>
        {projects.map((p) => (
          <option key={p.id} value={p.id}>{p.name}</option>
        ))}
      </select>

      <select
        className="wf-trigger-repos-repo"
        value={entry.repositoryId}
        onChange={(e) => onPickRepo(e.target.value)}
        aria-label="Repository"
      >
        <option value="">Pick a repository…</option>
        {visibleRepos.map((r) => (
          <option key={r.id} value={r.id}>{r.fullPath}</option>
        ))}
      </select>

      <LabelChipsInput
        value={entry.labels ?? []}
        onChange={onChangeLabels}
      />

      <button
        type="button"
        className="wf-trigger-repos-remove"
        onClick={onRemove}
        aria-label="Remove repository"
        title="Remove"
      >
        <Ic.X size={10} />
      </button>
    </div>
  );
}

interface LabelChipsInputProps {
  value: string[];
  onChange: (next: string[]) => void;
}

/**
 * Lightweight chip editor — comma- or Enter-delimited labels. Each chip has its
 * own × button. Deliberately doesn't fetch the repo's actual label list from the
 * provider: that would require a credential probe per row (slow) and the matcher
 * doesn't care whether a configured label "exists" on the remote — a PR that
 * never carries that label just never matches, which is the correct semantic.
 */
function LabelChipsInput({ value, onChange }: LabelChipsInputProps) {
  const add = (raw: string) => {
    const trimmed = raw.trim();
    if (!trimmed) return;
    if (value.includes(trimmed)) return;
    onChange([...value, trimmed]);
  };

  const remove = (label: string) => onChange(value.filter((l) => l !== label));

  return (
    <div className="wf-trigger-repos-labels" data-testid="trigger-repositories-labels">
      {value.map((label) => (
        <span key={label} className="wf-trigger-repos-label">
          <span>{label}</span>
          <button
            type="button"
            className="wf-trigger-repos-label-remove"
            onClick={() => remove(label)}
            aria-label={`Remove label ${label}`}
          ><Ic.X size={8} /></button>
        </span>
      ))}
      <input
        type="text"
        className="wf-trigger-repos-label-input"
        placeholder={value.length === 0 ? "Add label…" : ""}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === ",") {
            e.preventDefault();
            add(e.currentTarget.value);
            e.currentTarget.value = "";
          } else if (e.key === "Backspace" && e.currentTarget.value === "" && value.length > 0) {
            remove(value[value.length - 1]!);
          }
        }}
        onBlur={(e) => {
          if (e.currentTarget.value) {
            add(e.currentTarget.value);
            e.currentTarget.value = "";
          }
        }}
      />
    </div>
  );
}
