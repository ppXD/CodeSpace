import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useProjects } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";
import { normaliseRepositoriesArray, type TriggerRepoEntry } from "@/lib/migrateTriggerConfig";

/**
 * Trigger-inspector picker for the <c>repositories</c> property of the PR-trigger
 * activation config (PR #23). Renders a list editor + an explicit
 * "Match every repository" opt-in checkbox above it:
 *
 *   ☐ Match every repository in this team
 *   ┌─ card ──────────────────────────────────────────────────────┐
 *   │  Project: [Backend ▾]   Repository: [api ▾]            [×] │
 *   │  Labels (PR must carry all):  [bug ×]  [release ×]  ⌷      │
 *   ├──────────────────────────────────────────────────────────────┤
 *   │  + Add repository                                            │
 *   └──────────────────────────────────────────────────────────────┘
 *
 * <h3>Safe default: empty list = match nothing</h3>
 * A fresh trigger renders with the checkbox UNCHECKED and an empty list. The
 * picker emits <c>[]</c> in that state, which the matcher (precedence rule #1
 * iterating zero entries) treats as "no match". Operators can't accidentally
 * dispatch a workflow across every PR in the team just by dropping the trigger
 * node and saving.
 *
 * <h3>Explicit opt-in for team-wide triggers</h3>
 * Checking "Match every repository" emits <c>undefined</c> for the property.
 * The SchemaForm spreads <c>repositories: undefined</c>; JSON.stringify drops
 * the key on save → matcher rule #4 (empty config) → match-all. The checkbox
 * is the ONLY way to produce that wire state from the UI; API callers can
 * still do it by omitting the key.
 *
 * <h3>State derivation</h3>
 *   value === undefined → checkbox checked (match all)
 *   value === []        → unchecked, empty list (match nothing)
 *   value === [entries] → unchecked, populated list (match those entries)
 *
 * <h3>Defensive parsing</h3>
 * Raw <c>unknown</c> input flows through <see cref="normaliseRepositoriesArray"/>
 * which tolerates malformed entries and keeps in-progress empty-id rows so
 * the "Add → Pick" flow survives a re-render.
 */

interface TriggerRepositoriesSelectorProps {
  /** Property value: the repositories array, or undefined when the operator
   *  has opted into "match every repository". May be malformed shape from a
   *  hand-edited DB row — normaliseRepositoriesArray handles it. */
  value: unknown;
  /** Emit the new array OR undefined. Undefined causes the SchemaForm to
   *  spread `repositories: undefined`, dropping the key on JSON serialise —
   *  the matcher then sees an empty config and matches all (its precedence
   *  rule #4). The picker only emits undefined when the "Match every
   *  repository" checkbox is checked; clearing all rows emits `[]` so the
   *  safe-default (match nothing) holds. */
  onChange: (next: TriggerRepoEntry[] | undefined) => void;
}

export function TriggerRepositoriesSelector({ value, onChange }: TriggerRepositoriesSelectorProps) {
  // value === undefined ⇒ operator opted into "match every repository". The
  // wire format encodes this by omitting the `repositories` key entirely.
  const matchAll = value === undefined;
  const entries = useMemo(() => normaliseRepositoriesArray(value), [value]);
  const projects = useProjects();
  const repositories = useRepositories();

  const projectRows = useMemo(() => projects.data ?? [], [projects.data]);
  const repoRows = useMemo(() => repositories.data ?? [], [repositories.data]);

  // Per-row "draft" project picks. Stored in component state — NOT in the
  // saved shape — because the row's wire format is `{ repositoryId, labels? }`
  // and the project is purely a UI narrowing aid.
  const [draftProjectByIndex, setDraftProjectByIndex] = useState<Map<number, string>>(new Map());

  const projectForRow = (idx: number, entry: TriggerRepoEntry): string => {
    const draft = draftProjectByIndex.get(idx);
    if (draft !== undefined) return draft;
    if (!entry.repositoryId) return "";
    return repoRows.find((r) => r.id === entry.repositoryId)?.projects?.[0]?.id ?? "";
  };

  const toggleMatchAll = (next: boolean) => {
    if (next) onChange(undefined);   // opt-in: drop the key → matcher match-all
    else onChange([]);               // opt-out: explicit empty list → matcher match-nothing (safe)
  };

  const addRow = () => onChange([...entries, { repositoryId: "" }]);

  const removeRow = (idx: number) => {
    // Shift draft project indices: drop the removed entry, then re-key
    // everything above it down by one so a surviving row's UI state follows it.
    setDraftProjectByIndex((prev) => {
      const next = new Map<number, string>();
      for (const [k, v] of prev) {
        if (k < idx) next.set(k, v);
        else if (k > idx) next.set(k - 1, v);
      }
      return next;
    });
    // Even when removing the LAST row we emit `[]` (not undefined). The
    // checkbox is the ONLY way to get to undefined; otherwise removing the
    // last row would silently flip the trigger to fire on every repo —
    // exactly the footgun this PR was designed to remove.
    onChange(entries.filter((_, i) => i !== idx));
  };

  const updateRow = (idx: number, patch: Partial<TriggerRepoEntry>) => {
    onChange(entries.map((entry, i) => (i === idx ? { ...entry, ...patch } : entry)));
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
    <div className="wf-trigger-repos" data-testid="trigger-repositories-selector">
      <label className="wf-trigger-repos-matchall">
        <input
          type="checkbox"
          checked={matchAll}
          onChange={(e) => toggleMatchAll(e.target.checked)}
          aria-label="Match every repository"
        />
        <span className="wf-trigger-repos-matchall-label">Match every repository in this team</span>
      </label>

      {!matchAll && (
        <div className="wf-trigger-repos-card" data-testid="trigger-repositories-list">
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
      )}

      {!matchAll && entries.length === 0 && (
        <div className="wf-trigger-repos-hint">
          <span aria-hidden="true">ⓘ</span>
          <span>Fires on no repositories.</span>
        </div>
      )}

      {matchAll && (
        <div className="wf-trigger-repos-hint">
          <span aria-hidden="true">ⓘ</span>
          <span>Fires on every repository in this team.</span>
        </div>
      )}
    </div>
  );
}

interface TriggerRepoRowProps {
  entry: TriggerRepoEntry;
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
        <span
          className="wf-trigger-repos-field-label"
          title="PR must carry every listed label (AND match)"
        >Labels:</span>
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
