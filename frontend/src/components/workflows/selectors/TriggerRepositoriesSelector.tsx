import { useMemo, useState } from "react";

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
 * <c>{ repositories: [{ repositoryId, labels? }] }</c> config shape (PR #23).
 * Renders a list editor:
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
 * <h3>Empty list = match-all</h3>
 * When the operator clears every row, the picker emits <c>{}</c> (no
 * <c>repositories</c> key at all) so the matcher's empty-config → match-all
 * path fires. The explicit <c>{ repositories: [] }</c> "match nothing" shape
 * still exists at the wire level for API callers; the picker just never
 * produces it.
 *
 * <h3>Auto-migration</h3>
 * Incoming <c>value</c> may be the legacy <c>{ repositoryId, labels? }</c>
 * (configs saved before PR #23) or <c>{ repositories: [] }</c> from an
 * intermediate save. The selector normalises through
 * <see cref="migrateLegacyTriggerConfig"/> for display; the first
 * <c>onChange</c> emits the shape this picker prefers, so storage
 * transparently upgrades on first save.
 */

interface TriggerRepositoriesSelectorProps {
  value: unknown;
  onChange: (next: TriggerConfigArrayShape | Record<string, never>) => void;
}

export function TriggerRepositoriesSelector({ value, onChange }: TriggerRepositoriesSelectorProps) {
  const shape = useMemo(() => migrateLegacyTriggerConfig(value), [value]);
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

  // Emit `{}` when the list is empty so the matcher's empty-config → match-all
  // path fires (see component-level doc). Emit the full shape when there's at
  // least one row (including in-progress empty-repositoryId rows; the matcher
  // tolerates them as "no match" until the operator picks the repo).
  const emit = (next: TriggerRepoEntry[]) => {
    if (next.length === 0) onChange({});
    else onChange({ repositories: next });
  };

  const addRow = () => emit([...shape.repositories, { repositoryId: "" }]);

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
    emit(shape.repositories.filter((_, i) => i !== idx));
  };

  const updateRow = (idx: number, patch: Partial<TriggerRepoEntry>) => {
    const merged = shape.repositories.map((entry, i) => (i === idx ? { ...entry, ...patch } : entry));
    emit(merged);
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
        {shape.repositories.map((entry, idx) => (
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
