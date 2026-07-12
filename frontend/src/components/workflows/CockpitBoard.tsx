import { Ic } from "@/_imported/ai-code-space/icons";
import type { PendingDecision, RunPhasesResponse, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";
import { relativeTime } from "@/lib/codeTree";
import { statusWord } from "@/lib/runStatus";

import { DecisionCard } from "./DecisionCard";
import { Pager } from "./Pager";
import { compactAge, humanizeRunError, runDuration, runStatusTone, type CockpitFilter } from "./cockpit";
import { runKindLabel, sourceLabel } from "./runsIndex";
import { summarizeRunState } from "./runPhases";

/** How many suspended runs the default-board Needs-attention zone previews before it collapses to "View all N". */
const ATTENTION_PREVIEW = 5;

/** The cockpit's paginated History view — one numbered page of the team's terminal runs, with the total for the pager. */
export interface RunHistoryView {
  items: WorkflowRunSummary[];
  total: number;
  page: number;
  pageSize: number;
  isLoading: boolean;
  onPage: (page: number) => void;
}

/**
 * The Needs-attention view — the suspended runs that need a human (its OWN fetched set, not a slice of the newest-50),
 * plus the TRUE total for the card + the "View all N" affordance. The number, the preview, and the click-through are
 * three projections of this one set, so they can never disagree.
 */
export interface RunAttentionView {
  runs: WorkflowRunSummary[];
  total: number;
}

/**
 * The cockpit's work board — the zones below the status cards. Default (no filter armed): Needs attention (the action
 * zone — answerable decisions + suspended runs that need a look), then Live (each run's current state sentence), then
 * Recent (compact history). Arming a card narrows to one view. Decisions answer inline; suspended runs open the Run
 * Room (the right resume affordance depends on the wait kind, so we send the operator there rather than guess).
 */
export function CockpitBoard({ runs, decisions, live, attention, phasesByRun, filter, history, nowMs, onOpen, onFilter, repoName }: {
  runs: readonly WorkflowRunSummary[];
  decisions: readonly PendingDecision[];
  live: readonly WorkflowRunSummary[];
  attention: RunAttentionView;
  phasesByRun: Map<string, RunPhasesResponse>;
  filter: CockpitFilter;
  history: RunHistoryView;
  nowMs: number;
  onOpen: (run: WorkflowRunSummary) => void;
  onFilter: (filter: CockpitFilter) => void;
  /** Resolves a launch-scope repository id to its display name (from the already-loaded team repo set); the row shows a repo chip for the ones that resolve. */
  repoName?: (id: string) => string | undefined;
}) {
  if (filter === "failed") {
    return <Zone label="Failed"><CompactList runs={runs.filter((r) => r.status === "Failure")} nowMs={nowMs} onOpen={onOpen} repoName={repoName} empty="Nothing failed." /></Zone>;
  }
  if (filter === "today") {
    return <Zone label="Today"><CompactList runs={runs.filter((r) => isToday(r.createdDate, nowMs))} nowMs={nowMs} onOpen={onOpen} repoName={repoName} empty="No runs today yet." /></Zone>;
  }

  const showAttention = filter === null || filter === "attention";
  const showLive = filter === null || filter === "live";
  const showRecent = filter === null;

  // The attention zone previews the top N suspended runs on the default board, the whole (fetched) set when armed; the
  // count + the rows are the same set, so "View all" appears exactly when the true total exceeds what's shown.
  const attnShown = filter === "attention" ? attention.runs : attention.runs.slice(0, ATTENTION_PREVIEW);
  const attnMore = filter === null && attention.total > ATTENTION_PREVIEW;

  return (
    <div className="cockpit-board">
      {showAttention && (
        <Zone label="Needs attention">
          {decisions.length === 0 && attention.runs.length === 0
            ? <div className="cockpit-empty"><Ic.Check size={13} /> Nothing needs you right now.</div>
            : (
              <div className="cockpit-attention">
                {decisions.map((d) => <DecisionCard key={d.id} decision={d} />)}
                {attnShown.map((r) => <SuspendedRow key={r.id} run={r} nowMs={nowMs} onOpen={onOpen} />)}
                {attnMore && (
                  <button type="button" className="cockpit-viewall" onClick={() => onFilter("attention")}>
                    View all {decisions.length + attention.total} <Ic.ChevronRight size={13} />
                  </button>
                )}
                {/* The fetched set is capped (a pathological 50+ suspended-needing-review); never over-promise the count. */}
                {filter === "attention" && attention.total > attention.runs.length && (
                  <div className="cockpit-zone-note">Showing the first {attention.runs.length} of {attention.total} — narrow the scope to see the rest.</div>
                )}
              </div>
            )}
        </Zone>
      )}

      {showLive && live.length > 0 && (
        <Zone label="Live">
          <div className="cockpit-live">
            {live.map((r) => <LiveRow key={r.id} run={r} phases={phasesByRun.get(r.id)} nowMs={nowMs} onOpen={onOpen} />)}
          </div>
        </Zone>
      )}

      {showRecent && (
        <Zone label="History">
          {history.isLoading && history.items.length === 0
            ? <div className="cockpit-empty">Loading…</div>
            : (
              <>
                <CompactList runs={history.items} nowMs={nowMs} onOpen={onOpen} repoName={repoName} empty="No past runs yet." />
                <Pager page={history.page} pageSize={history.pageSize} total={history.total} onPage={history.onPage} />
              </>
            )}
        </Zone>
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

// Every cockpit row represents a LINEAGE, shown by its ORIGINAL run's identity — so opening a row that's actually the
// latest rerun lands on the original (no confusing "Replay of …"), and a reran task titles as the task, not "Replay".
/** The lineage's display title — the workflow name for an authored run, else the launching task's session title, else a
 *  neutral fallback. NEVER the raw source token ("Snapshot" / "Manual"), which names how the engine stored the run, not the work. */
function lineageTitle(run: WorkflowRunSummary): string { return run.workflowName ?? run.sessionTitle ?? "Untitled task"; }
/** True when this row's representative is itself a rerun fork (its root is a different run) — drives the "rerunning" brief. */
function isRerun(run: WorkflowRunSummary): boolean { return run.rootRunId !== run.id; }

/** A suspended run in the attention zone — its wait, how long it has been parked, and a Review action into the Run Room. */
function SuspendedRow({ run, nowMs, onOpen }: { run: WorkflowRunSummary; nowMs: number; onOpen: (run: WorkflowRunSummary) => void }) {
  const title = lineageTitle(run);

  return (
    <div className="cockpit-attn-row" onClick={() => onOpen(run)}>
      <span className="run-row2-tile" data-tone="suspended" aria-hidden="true"><Ic.Pause size={13} /></span>
      <div className="cockpit-attn-body">
        <div className="cockpit-attn-title">{title} <span className="cockpit-attn-sub">suspended</span></div>
        <div className="cockpit-attn-meta">{sourceLabel(run.rootSourceType)} · waiting {compactAge(run.startedAt ?? run.createdDate, nowMs)}</div>
      </div>
      <button type="button" className="btn cockpit-attn-act" onClick={(e) => { e.stopPropagation(); onOpen(run); }}>Review →</button>
    </div>
  );
}

/** A live run's current-state sentence — derived from its phase projection (focus phase + agents) when loaded. */
function LiveRow({ run, phases, nowMs, onOpen }: { run: WorkflowRunSummary; phases?: RunPhasesResponse; nowMs: number; onOpen: (run: WorkflowRunSummary) => void }) {
  const title = lineageTitle(run);
  const rerunning = isRerun(run);   // this live run is a rerun fork — its phases (the meta) say which node is rerunning
  const state = phases ? summarizeRunState(phases.runStatus, phases.phases) : null;
  const elapsed = run.startedAt ? compactAge(run.startedAt, nowMs) : null;

  const parts = [
    state?.focus,
    state && state.totalAgents > 0 ? `${state.activeAgents} of ${state.totalAgents} agents active` : null,
    elapsed,
  ].filter(Boolean);

  return (
    <div className="cockpit-live-row" onClick={() => onOpen(run)}>
      <span className="run-row2-tile" data-tone="running" aria-hidden="true"><span className="runs-row-spin" /></span>
      <div className="cockpit-live-body">
        <div className="cockpit-live-title">
          <span className="cockpit-live-name">{title}</span>
          {rerunning && <span className="cockpit-live-rerun"><Ic.Branch size={10} aria-hidden="true" /> rerunning · attempt {run.attemptCount}</span>}
        </div>
        <div className="cockpit-live-meta">{parts.length > 0 ? parts.join(" · ") : run.status}</div>
      </div>
      <span className="cockpit-live-status">{run.status}</span>
    </div>
  );
}

/** The run list used for History + the failed/today filter views — info-dense two-line rows. */
function CompactList({ runs, nowMs, onOpen, repoName, empty }: { runs: WorkflowRunSummary[]; nowMs: number; onOpen: (run: WorkflowRunSummary) => void; repoName?: (id: string) => string | undefined; empty: string }) {
  if (runs.length === 0) return <div className="cockpit-empty">{empty}</div>;

  return (
    <ul className="runs-list">
      {runs.map((r) => <RunRow key={r.id} run={r} nowMs={nowMs} onOpen={onOpen} repoName={repoName} />)}
    </ul>
  );
}

/**
 * A run row, reading top-down: the run NAME with its Workflow/Task type + version as labels beside it (and when it
 * ran on the right); then the status word in its tone, the launch repository, and the run's wall-clock duration; and,
 * only for a failed run, a third line that boxes the error in a red label sized to the message. The status tone is
 * carried by a tinted tile on the left and the status word, so the state reads at a glance without a separate badge column.
 */
function RunRow({ run, nowMs, onOpen, repoName }: { run: WorkflowRunSummary; nowMs: number; onOpen: (run: WorkflowRunSummary) => void; repoName?: (id: string) => string | undefined }) {
  const title = lineageTitle(run);
  const tone = runStatusTone(run.status);
  const version = run.workflowVersion != null ? `v${run.workflowVersion}` : null;
  const duration = runDuration(run, nowMs);
  // A parked-then-terminal run shows a lifespan ("open 5d"), not a runtime — so it drops the clock glyph (which would imply work).
  const lifespan = run.wasSuspended && (run.status === "Success" || run.status === "Failure" || run.status === "Cancelled");
  const when = run.completedAt ?? run.startedAt ?? run.createdDate;
  const error = run.status === "Failure" ? run.error : null;
  // Launch-scope repos resolved to names from the team set; unresolved ids (archived / not yet loaded) drop out silently.
  const repos = repoName ? run.repositoryIds.map(repoName).filter((n): n is string => !!n) : [];

  return (
    <li className="run-row2" onClick={() => onOpen(run)}>
      <span className="run-row2-tile" data-tone={tone} aria-hidden="true"><RunGlyph status={run.status} /></span>
      <div className="run-row2-body">
        <div className="run-row2-l1">
          <span className="run-row2-title" title={title}>{title}</span>
          <span className="run-row2-type" data-type={run.runKind}>{runKindLabel(run.runKind)}</span>
          {version && <span className="run-row2-ver">{version}</span>}
          {run.attemptCount > 1 && (
            <span className="run-row2-attempts" title={`${run.attemptCount} attempts — showing the latest`}>
              <Ic.Branch size={10} aria-hidden="true" />{run.attemptCount} attempts
            </span>
          )}
          <span className="run-row2-gap" />
          <span className="run-row2-when">{relativeTime(when)}</span>
        </div>
        <div className="run-row2-l2">
          <span className="run-row2-sw" data-tone={tone}>{statusWord(run.status)}</span>
          {repos.length > 0 && (
            <span className="run-row2-repo" title={repos.join(", ")}>
              <Ic.Repo size={11} aria-hidden="true" />
              <span className="run-row2-repo-name">{repos[0]}</span>
              {repos.length > 1 && <span className="run-row2-repo-more">+{repos.length - 1}</span>}
            </span>
          )}
          <span className="run-row2-gap" />
          {duration && <span className="run-row2-dur">{!lifespan && <Ic.Clock size={11} />}{duration}</span>}
        </div>
        {error && (
          <div className="run-row2-l3">
            <span className="run-row2-err" title={error}><Ic.Triangle size={12} /><span>{humanizeRunError(error)}</span></span>
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
