import { Ic } from "@/_imported/ai-code-space/icons";
import type { PendingDecision, RunPhasesResponse, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";
import { relativeTime } from "@/lib/codeTree";

import { DecisionCard } from "./DecisionCard";
import { compactAge, runDuration, runStatusTone, runStatusWord, runType, suspendedNeedingReview, type CockpitFilter } from "./cockpit";
import { bucketRuns, sourceLabel } from "./runsIndex";
import { summarizeRunState } from "./runPhases";

/**
 * The cockpit's work board — the zones below the status cards. Default (no filter armed): Needs attention (the action
 * zone — answerable decisions + suspended runs that need a look), then Live (each run's current state sentence), then
 * Recent (compact history). Arming a card narrows to one view. Decisions answer inline; suspended runs open the Run
 * Room (the right resume affordance depends on the wait kind, so we send the operator there rather than guess).
 */
export function CockpitBoard({ runs, decisions, phasesByRun, filter, nowMs, onOpen }: {
  runs: readonly WorkflowRunSummary[];
  decisions: readonly PendingDecision[];
  phasesByRun: Map<string, RunPhasesResponse>;
  filter: CockpitFilter;
  nowMs: number;
  onOpen: (runId: string) => void;
}) {
  const buckets = bucketRuns(runs);

  // The exact set the Needs-attention CARD counts (decisions + these), so the headline can't disagree with the rows.
  const suspended = suspendedNeedingReview(runs, decisions);

  if (filter === "failed") {
    return <Zone label="Failed / stuck"><CompactList runs={runs.filter((r) => r.status === "Failure" || r.status === "Suspended")} nowMs={nowMs} onOpen={onOpen} empty="Nothing failed or stuck." /></Zone>;
  }
  if (filter === "today") {
    return <Zone label="Today"><CompactList runs={runs.filter((r) => isToday(r.createdDate, nowMs))} nowMs={nowMs} onOpen={onOpen} empty="No runs today yet." /></Zone>;
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
                {suspended.map((r) => <SuspendedRow key={r.id} run={r} nowMs={nowMs} onOpen={onOpen} />)}
              </div>
            )}
        </Zone>
      )}

      {showLive && buckets.live.length > 0 && (
        <Zone label="Live">
          <div className="cockpit-live">
            {buckets.live.map((r) => <LiveRow key={r.id} run={r} phases={phasesByRun.get(r.id)} nowMs={nowMs} onOpen={onOpen} />)}
          </div>
        </Zone>
      )}

      {showRecent && (
        <Zone label="History"><CompactList runs={buckets.recent} nowMs={nowMs} onOpen={onOpen} empty="No runs yet." /></Zone>
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
function SuspendedRow({ run, nowMs, onOpen }: { run: WorkflowRunSummary; nowMs: number; onOpen: (runId: string) => void }) {
  const title = run.workflowName ?? sourceLabel(run.sourceType);

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
function LiveRow({ run, phases, nowMs, onOpen }: { run: WorkflowRunSummary; phases?: RunPhasesResponse; nowMs: number; onOpen: (runId: string) => void }) {
  const title = run.workflowName ?? sourceLabel(run.sourceType);
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
function CompactList({ runs, nowMs, onOpen, empty }: { runs: WorkflowRunSummary[]; nowMs: number; onOpen: (runId: string) => void; empty: string }) {
  if (runs.length === 0) return <div className="cockpit-empty">{empty}</div>;

  return (
    <ul className="runs-list">
      {runs.map((r) => <RunRow key={r.id} run={r} nowMs={nowMs} onOpen={onOpen} />)}
    </ul>
  );
}

/**
 * A run row, reading top-down: the run NAME with its Workflow/Task type + version as labels beside it (and when it
 * ran on the right); then the status word in its tone + the run's wall-clock duration; and, only for a failed run,
 * a third line that boxes the error in a red label sized to the message. The status tone is carried by a tinted tile
 * on the left and the status word, so the state reads at a glance without a separate badge column.
 */
function RunRow({ run, nowMs, onOpen }: { run: WorkflowRunSummary; nowMs: number; onOpen: (runId: string) => void }) {
  const title = run.workflowName ?? sourceLabel(run.sourceType);
  const type = runType(run);
  const tone = runStatusTone(run.status);
  const version = run.workflowVersion != null ? `v${run.workflowVersion}` : null;
  const duration = runDuration(run, nowMs);
  const when = run.completedAt ?? run.startedAt ?? run.createdDate;
  const error = run.status === "Failure" ? run.error : null;

  return (
    <li className="run-row2" onClick={() => onOpen(run.id)}>
      <span className="run-row2-tile" data-tone={tone} aria-hidden="true"><RunGlyph status={run.status} /></span>
      <div className="run-row2-body">
        <div className="run-row2-l1">
          <span className="run-row2-title" title={title}>{title}</span>
          <span className="run-row2-type" data-type={type.toLowerCase()}>{type}</span>
          {version && <span className="run-row2-ver">{version}</span>}
          <span className="run-row2-gap" />
          <span className="run-row2-when">{relativeTime(when)}</span>
        </div>
        <div className="run-row2-l2">
          <span className="run-row2-sw" data-tone={tone}>{runStatusWord(run.status)}</span>
          {duration && <span className="run-row2-dur"><Ic.Clock size={11} />{duration}</span>}
          <span className="run-row2-gap" />
          <span className="run-row2-id">{run.id.slice(0, 8)}</span>
        </div>
        {error && (
          <div className="run-row2-l3">
            <span className="run-row2-err" title={error}><Ic.Triangle size={12} /><span>{error}</span></span>
          </div>
        )}
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
