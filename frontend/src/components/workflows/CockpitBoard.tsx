import { Ic } from "@/_imported/ai-code-space/icons";
import type { PendingDecision, RunPhasesResponse, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";
import { relativeTime } from "@/lib/codeTree";

import { DecisionCard } from "./DecisionCard";
import { compactAge, runOutcome, suspendedNeedingReview, type CockpitFilter } from "./cockpit";
import { bucketRuns, sourceLabel } from "./runsIndex";
import { summarizeRunState } from "./runPhases";

/**
 * The cockpit's work board — the zones below the status cards. Default (no filter armed): Needs attention (the action
 * zone — answerable decisions + suspended runs that need a look), then Live (each run's current state sentence), then
 * Recent (compact history). Arming a card narrows to one view. Decisions answer inline; suspended runs open the Run
 * Room (the right resume affordance depends on the wait kind, so we send the operator there rather than guess).
 */
export function CockpitBoard({ runs, decisions, phasesByRun, nameById, filter, nowMs, onOpen }: {
  runs: readonly WorkflowRunSummary[];
  decisions: readonly PendingDecision[];
  phasesByRun: Map<string, RunPhasesResponse>;
  nameById: Map<string, string>;
  filter: CockpitFilter;
  nowMs: number;
  onOpen: (runId: string) => void;
}) {
  const buckets = bucketRuns(runs);

  // The exact set the Needs-attention CARD counts (decisions + these), so the headline can't disagree with the rows.
  const suspended = suspendedNeedingReview(runs, decisions);

  if (filter === "failed") {
    return <Zone label="Failed / stuck"><CompactList runs={runs.filter((r) => r.status === "Failure" || r.status === "Suspended")} nameById={nameById} nowMs={nowMs} onOpen={onOpen} empty="Nothing failed or stuck." /></Zone>;
  }
  if (filter === "today") {
    return <Zone label="Today"><CompactList runs={runs.filter((r) => isToday(r.createdDate, nowMs))} nameById={nameById} nowMs={nowMs} onOpen={onOpen} empty="No runs today yet." /></Zone>;
  }

  const showAttention = filter === null || filter === "attention";
  const showLive = filter === null || filter === "live";
  const showRecent = filter === null;

  return (
    <div className="cockpit-board">
      {showAttention && (
        <Zone label="Needs attention">
          {decisions.length === 0 && suspended.length === 0
            ? <div className="cockpit-empty"><Ic.Check size={13} /> Nothing needs you right now.</div>
            : (
              <div className="cockpit-attention">
                {decisions.map((d) => <DecisionCard key={d.id} decision={d} />)}
                {suspended.map((r) => <SuspendedRow key={r.id} run={r} name={r.workflowId ? nameById.get(r.workflowId) : null} nowMs={nowMs} onOpen={onOpen} />)}
              </div>
            )}
        </Zone>
      )}

      {showLive && buckets.live.length > 0 && (
        <Zone label="Live">
          <div className="cockpit-live">
            {buckets.live.map((r) => <LiveRow key={r.id} run={r} phases={phasesByRun.get(r.id)} name={r.workflowId ? nameById.get(r.workflowId) : null} nowMs={nowMs} onOpen={onOpen} />)}
          </div>
        </Zone>
      )}

      {showRecent && (
        <Zone label="History"><CompactList runs={buckets.recent} nameById={nameById} nowMs={nowMs} onOpen={onOpen} empty="No runs yet." /></Zone>
      )}
    </div>
  );
}

function Zone({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <section className="cockpit-zone">
      <div className="cockpit-zone-head"><span className="cockpit-zone-label">{label}</span></div>
      {children}
    </section>
  );
}

/** A suspended run in the attention zone — its wait, how long it has been parked, and a Review action into the Run Room. */
function SuspendedRow({ run, name, nowMs, onOpen }: { run: WorkflowRunSummary; name?: string | null; nowMs: number; onOpen: (runId: string) => void }) {
  const title = name ?? sourceLabel(run.sourceType);

  return (
    <div className="cockpit-attn-row" onClick={() => onOpen(run.id)}>
      <span className="cockpit-attn-glyph" data-tone="suspended" aria-hidden="true"><Ic.Pause size={12} /></span>
      <div className="cockpit-attn-body">
        <div className="cockpit-attn-title">{title} <span className="cockpit-attn-sub">suspended</span></div>
        <div className="cockpit-attn-meta">{sourceLabel(run.sourceType)} · waiting {compactAge(run.startedAt ?? run.createdDate, nowMs)}</div>
      </div>
      <button type="button" className="btn cockpit-attn-act" onClick={(e) => { e.stopPropagation(); onOpen(run.id); }}>Review →</button>
    </div>
  );
}

/** A live run's current-state sentence — derived from its phase projection (focus phase + agents) when loaded. */
function LiveRow({ run, phases, name, nowMs, onOpen }: { run: WorkflowRunSummary; phases?: RunPhasesResponse; name?: string | null; nowMs: number; onOpen: (runId: string) => void }) {
  const title = name ?? sourceLabel(run.sourceType);
  const state = phases ? summarizeRunState(phases.runStatus, phases.phases) : null;
  const elapsed = run.startedAt ? compactAge(run.startedAt, nowMs) : null;

  const parts = [
    state?.focus,
    state && state.totalAgents > 0 ? `${state.activeAgents} of ${state.totalAgents} agents active` : null,
    elapsed,
  ].filter(Boolean);

  return (
    <div className="cockpit-live-row" onClick={() => onOpen(run.id)}>
      <span className="cockpit-live-dot" aria-hidden="true" />
      <div className="cockpit-live-body">
        <div className="cockpit-live-title">{title}</div>
        <div className="cockpit-live-meta">{parts.length > 0 ? parts.join(" · ") : run.status}</div>
      </div>
      <span className="cockpit-live-status">{run.status}</span>
    </div>
  );
}

/** The run list used for History + the failed/today filter views — info-dense two-line rows. */
function CompactList({ runs, nameById, nowMs, onOpen, empty }: { runs: WorkflowRunSummary[]; nameById: Map<string, string>; nowMs: number; onOpen: (runId: string) => void; empty: string }) {
  if (runs.length === 0) return <div className="cockpit-empty">{empty}</div>;

  return (
    <ul className="runs-list">
      {runs.map((r) => <RunRow key={r.id} run={r} name={r.workflowId ? nameById.get(r.workflowId) : null} nowMs={nowMs} onOpen={onOpen} />)}
    </ul>
  );
}

/**
 * Two-line run row: the title + when on top, and below it the run's context (kind · source · version) folded into a
 * RESULT summary ("completed in 7m59s" / "failed · …") plus its short id — so each row reads as "what happened",
 * not a bare DB cell. The status colour is carried by the glyph, so no redundant status word.
 */
function RunRow({ run, name, nowMs, onOpen }: { run: WorkflowRunSummary; name?: string | null; nowMs: number; onOpen: (runId: string) => void }) {
  const title = name ?? sourceLabel(run.sourceType);
  const meta = [run.workflowId ? "Workflow" : "Task", sourceLabel(run.sourceType), run.workflowVersion != null ? `v${run.workflowVersion}` : null, runOutcome(run, nowMs)].filter(Boolean).join(" · ");
  const when = run.completedAt ?? run.startedAt ?? run.createdDate;

  return (
    <li className="run-row2" data-status={run.status.toLowerCase()} onClick={() => onOpen(run.id)}>
      <span className="run-row2-glyph" data-status={run.status.toLowerCase()} aria-hidden="true"><RunGlyph status={run.status} /></span>
      <div className="run-row2-body">
        <div className="run-row2-l1">
          <span className="run-row2-title" title={title}>{title}</span>
          <span className="run-row2-when">{relativeTime(when)}</span>
        </div>
        <div className="run-row2-l2">
          <span className="run-row2-meta" title={meta}>{meta}</span>
          <span className="run-row2-id">{run.id.slice(0, 8)}</span>
        </div>
      </div>
    </li>
  );
}

/** The run's status glyph — Running is a spinner, Suspended a pause, everything else a tone-coloured mark. */
function RunGlyph({ status }: { status: WorkflowRunStatus }) {
  if (status === "Success") return <Ic.Check size={13} />;
  if (status === "Failure") return <Ic.X size={13} />;
  if (status === "Cancelled") return <Ic.Dot size={15} />;
  if (status === "Running") return <span className="runs-row-spin" />;
  if (status === "Suspended") return <Ic.Pause size={12} />;
  return <span className="runs-row-hollow" />;   // Pending / Enqueued
}

/** True when an ISO timestamp falls on the local day of `nowMs`. */
function isToday(iso: string, nowMs: number): boolean {
  const now = new Date(nowMs);
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  return new Date(iso).getTime() >= start;
}
