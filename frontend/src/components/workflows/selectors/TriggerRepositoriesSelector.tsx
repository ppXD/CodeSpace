import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";
import { normaliseRepositoriesArray, type TriggerRepoEntry } from "@/lib/migrateTriggerConfig";

/**
 * Trigger-inspector picker for the <c>repositories</c> property of the PR-trigger
 * activation config (PR #23). Renders a list editor:
 *
 *   ┌─ card ──────────────────────────────────────────────────────┐
 *   │  Project: [Backend ▾]   Repository: [api ▾]            [×] │
 *   │  Labels (PR must carry all):  [bug ×]  [release ×]  ⌷      │
 *   ├──────────────────────────────────────────────────────────────┤
 *   │  Project: [Backend ▾]   Repository: [cli ▾]            [×] │
 *   │  Labels (PR must carry all):  (none)                         │
 *   ├──────────────────────────────────────────────────────────────┤
 *   │  + Add repository                                            │
 *   └──────────────────────────────────────────────────────────────┘
 *   ⓘ Leave list empty to trigger on any repo in this team.
 *
 * The project dropdown narrows the repo dropdown (cascade) — picking
 * <c>frontend-platform</c> shows only repos linked to that project. This is
 * UX-only; the saved shape is still <c>{ repositoryId, labels }</c> per row,
 * no project-id stored. The matcher dispatches on repositoryId.
 *
 * <h3>Value contract</h3>
 * The schema declares <c>"x-selector": "trigger.repositories"</c> on the
 * <c>repositories</c> ARRAY property, so the SchemaForm passes us the array
 * directly (NOT the wrapping config object). <c>value</c> is therefore
 * <c>TriggerRepoEntry[] | undefined</c>; <c>onChange</c> emits a fresh array
 * or <c>undefined</c> when the list becomes empty (so the SchemaForm spreads
 * <c>repositories: undefined</c> which JSON.stringify drops on save —
 * routing the matcher through its empty-config → match-all path).
 *
 * <h3>Defensive parsing</h3>
 * Raw <c>unknown</c> input flows through <see cref="normaliseRepositoriesArray"/>
 * which tolerates malformed entries (skipping them) and keeps in-progress
 * empty-repositoryId rows so the "Add → Pick" flow survives a re-render.
 */

interface TriggerRepositoriesSelectorProps {
  /** Property value: the repositories array, or undefined when the operator
   *  has never picked anything. May be malformed shape from a hand-edited DB
   *  row — normaliseRepositoriesArray handles it. */
  value: unknown;
  /** Emit the new array, OR undefined when the list is empty. Undefined
   *  causes the SchemaForm to spread `repositories: undefined`, dropping
   *  the key on JSON serialise — the matcher then sees an empty config
   *  and matches all (its precedence rule #4). */
  onChange: (next: TriggerRepoEntry[] | undefined) => void;
}

export function TriggerRepositoriesSelector({ value, onChange }: TriggerRepositoriesSelectorProps) {
  const entries = useMemo(() => normaliseRepositoriesArray(value), [value]);
  const projects = useProjects();
  const repositories = useRepositories();

  const projectRows = useMemo(() => projects.data ?? [], [projects.data]);
  const repoRows = useMemo(() => repositories.data ?? [], [repositories.data]);

  // Per-row "draft" project picks. Stored in component state — NOT in the saved
  // shape — because the row's wire format is `{ repositoryId, labels? }` and the
  // project is purely a UI narrowing aid. Keyed by row index; on Add the new index
  // is implicitly missing (the row defaults to "All projects" until the operator
  // picks one). On Remove we shift the indices via filter+map to preserve picks
  // for rows that survived. Inferring a default from the row's existing repo
  // (when present) keeps a re-opened workflow showing the right project filter.
  const [draftProjectByIndex, setDraftProjectByIndex] = useState<Map<number, string>>(new Map());

  const projectForRow = (idx: number, entry: TriggerRepoEntry): string => {
    const draft = draftProjectByIndex.get(idx);
    if (draft !== undefined) return draft;
    if (!entry.repositoryId) return "";
    return repoRows.find((r) => r.id === entry.repositoryId)?.projects?.[0]?.id ?? "";
  };

  // Emit undefined when the list is empty so the SchemaForm spreads `repositories:
  // undefined`, dropping the key on JSON serialise → matcher empty-config path →
  // match-all. Emit the array when there's ≥1 row (including in-progress empty
  // entries; the matcher tolerates them).
  const emit = (next: TriggerRepoEntry[]) => onChange(next.length === 0 ? undefined : next);

  const addRow = () => emit([...entries, { repositoryId: "" }]);

  const removeRow = (idx: number) => {
    // Shift draft project indices: drop the removed entry, then re-key everything
    // above it down by one so a surviving row's UI state follows it.
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

  const updateRow = (idx: number, patch: Partial<TriggerRepoEntry>) => {
    emit(entries.map((entry, i) => (i === idx ? { ...entry, ...patch } : entry)));
  };

  const pickProjectForRow = (idx: number, projectId: string) => {
    setDraftProjectByIndex((prev) => {
      const next = new Map(prev);
      if (projectId === "") next.delete(idx);
      else next.set(idx, projectId);
      return next;
    });
    // Clearing the row's repo whenever the project changes prevents a stale repo
    // from a different project lingering in the saved shape.
    updateRow(idx, { repositoryId: "" });
  };

  return (
    <div className="wf-trigger-repos" data-testid="trigger-repositories-selector">
      <div className="wf-trigger-repos-card">
        {entries.map((entry, idx) => (
          <TriggerRepoRow
            key={idx}
            entry={entry}
            projectId={projectForRow(idx, entry)}
            projects={projectRows}
            repositories={repoRows}
            onPickProject={(projectId) => pickProjectForRow(idx, projectId)}
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

      <div className="wf-trigger-repos-hint">
        <span aria-hidden="true">ⓘ</span>
        <span>Leave list empty to trigger on any repo in this team.</span>
      </div>
    </div>
  );
}

interface TriggerRepoRowProps {
  entry: TriggerRepoEntry;
  /** Picked project for THIS row — drives the repo dropdown's filter. Lives in the
   *  picker's component state, not the saved shape. Empty = "All projects". */
  projectId: string;
  projects: Array<{ id: string; name: string; slug: string }>;
  repositories: Array<{ id: string; fullPath: string; projects?: Array<{ id: string }> }>;
  onPickProject: (projectId: string) => void;
  onPickRepo: (repositoryId: string) => void;
  onChangeLabels: (labels: string[]) => void;
  onRemove: () => void;
}

function TriggerRepoRow({
  entry,
  projectId,
  projects,
  repositories,
  onPickProject,
  onPickRepo,
  onChangeLabels,
  onRemove,
}: TriggerRepoRowProps) {
  const visibleRepos = useMemo(() => {
    if (!projectId) return repositories;
    return repositories.filter((r) => (r.projects ?? []).some((p) => p.id === projectId));
  }, [repositories, projectId]);

  return (
    <div className="wf-trigger-repos-row" data-testid="trigger-repositories-row">
      <div className="wf-trigger-repos-row-controls">
        <label className="wf-trigger-repos-field">
          <span className="wf-trigger-repos-field-label">Project:</span>
          <select
            className="wf-trigger-repos-select"
            value={projectId}
            onChange={(e) => onPickProject(e.target.value)}
            aria-label="Project"
          >
            <option value="">All projects</option>
            {projects.map((p) => (
              <option key={p.id} value={p.id}>{p.name}</option>
            ))}
          </select>
        </label>

        <label className="wf-trigger-repos-field">
          <span className="wf-trigger-repos-field-label">Repository:</span>
          <select
            className="wf-trigger-repos-select"
            value={entry.repositoryId}
            onChange={(e) => onPickRepo(e.target.value)}
            aria-label="Repository"
          >
            <option value="">Pick a repository…</option>
            {visibleRepos.map((r) => (
              <option key={r.id} value={r.id}>{r.fullPath}</option>
            ))}
          </select>
        </label>

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

      <div className="wf-trigger-repos-row-labels">
        <span className="wf-trigger-repos-field-label">Labels (PR must carry all):</span>
        <LabelChipsInput
          value={entry.labels ?? []}
          onChange={onChangeLabels}
        />
      </div>
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
      {value.length === 0 && (
        <span className="wf-trigger-repos-labels-empty">(none)</span>
      )}
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
        placeholder={value.length === 0 ? "Add label…" : "+"}
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
