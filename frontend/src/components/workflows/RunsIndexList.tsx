import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";
import { relativeTime } from "@/lib/codeTree";

import { bucketRuns, sourceLabel } from "./runsIndex";

/**
 * The Runs index body — the team's runs split into three zones read top-to-bottom: Needs attention (parked on a
 * human signal), Live (in flight), Recent (settled). Each row opens the Run Room via `onOpen`. Presentational +
 * router-free (the route owns the query + navigation), so it unit-tests directly.
 */
export function RunsZones({ runs, nameById, onOpen }: {
  runs: readonly WorkflowRunSummary[];
  nameById: Map<string, string>;
  onOpen: (runId: string) => void;
}) {
  const buckets = bucketRuns(runs);

  return (
    <div className="runs-zones">
      <Zone label="Needs attention" tone="attention" runs={buckets.needsAttention} nameById={nameById} onOpen={onOpen} />
      <Zone label="Live" tone="live" runs={buckets.live} nameById={nameById} onOpen={onOpen} />
      <Zone label="Recent" tone="recent" runs={buckets.recent} nameById={nameById} onOpen={onOpen} />
    </div>
  );
}

/** One zone — hidden entirely when empty, so the page only shows the bands that have runs. */
function Zone({ label, tone, runs, nameById, onOpen }: {
  label: string; tone: string; runs: WorkflowRunSummary[]; nameById: Map<string, string>; onOpen: (runId: string) => void;
}) {
  if (runs.length === 0) return null;

  return (
    <section className="runs-zone" data-tone={tone}>
      <div className="runs-zone-head">
        <span className="runs-zone-label">{label}</span>
        <span className="runs-zone-count">{runs.length}</span>
      </div>
      <ul className="runs-list">
        {runs.map((r) => <RunRow key={r.id} run={r} name={r.workflowId ? nameById.get(r.workflowId) : null} onOpen={onOpen} />)}
      </ul>
    </section>
  );
}

function RunRow({ run, name, onOpen }: { run: WorkflowRunSummary; name?: string | null; onOpen: (runId: string) => void }) {
  const title = name ?? sourceLabel(run.sourceType);
  const when = run.startedAt ?? run.createdDate;

  return (
    <li className="runs-row" data-status={run.status.toLowerCase()} onClick={() => onOpen(run.id)}>
      <span className="runs-row-glyph" data-status={run.status.toLowerCase()} aria-hidden="true"><RunGlyph status={run.status} /></span>
      <span className="runs-row-title" title={title}>{title}</span>
      <span className="runs-row-id">{run.id.slice(0, 8)}</span>
      <span className="runs-row-source">{sourceLabel(run.sourceType)}</span>
      <span className="runs-row-status">{run.status}</span>
      <span className="runs-row-when">{relativeTime(when)}</span>
    </li>
  );
}

/** The run's status glyph — the same six-state vocabulary the run views use (Running is a spinner, Suspended a pause). */
function RunGlyph({ status }: { status: WorkflowRunStatus }) {
  if (status === "Success") return <Ic.Check size={13} />;
  if (status === "Failure") return <Ic.X size={13} />;
  if (status === "Cancelled") return <Ic.Dot size={15} />;
  if (status === "Running") return <span className="runs-row-spin" />;
  if (status === "Suspended") return <Ic.Pause size={12} />;
  return <span className="runs-row-hollow" />;   // Pending / Enqueued
}
