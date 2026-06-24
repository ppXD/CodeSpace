import { useMemo, useState, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { isAgentRunActive, type AgentRunStatus } from "@/api/agents";
import type { WorkflowRunNodeSummary, WorkflowRunWaitInfo } from "@/api/workflows";
import { ApiError } from "@/api/request";
import { useAgentRun } from "@/hooks/use-agents";
import { useNodeManifests, useResumeRun, useRunPhases, useWorkflowRun } from "@/hooks/use-workflows";

import { AgentRunTimeline } from "./AgentRunTimeline";
import { AgentToolCalls } from "./AgentToolCalls";
import { JsonView } from "./JsonView";
import { RunActivityTiles } from "./RunActivityTiles";
import { RunCanvas } from "./RunCanvas";
import { RunStatusBadge } from "./RunStatusBadge";
import { RunTrace } from "./RunTrace";
import { dedupRunAgents } from "./runPhases";
import { branchBadge, groupMapBranches, type MapRollup } from "./mapBranches";
import { concurrentNodeKeys, runNodeKey } from "./runConcurrency";

export { RunStatusBadge };

/**
 * How many sub-workflow levels we embed inline before falling back to a plain id. The engine caps
 * real nesting at 8; embedding (each level is a live, polling RunDetailView) stops well before that.
 */
const MAX_EMBED_DEPTH = 3;

/**
 * Shared run-detail view: status summary + normalized payload + declared outputs + the
 * per-node execution trace for one workflow run. Fetches by id and auto-polls while the run
 * is non-terminal (via useWorkflowRun).
 *
 * Rendered both on the standalone run-detail route AND inside the editor's in-page run dialog,
 * so the two never drift. It deliberately uses the `.acs-root`-scoped `.wf-*` styles, so any
 * host must live inside `.acs-root` (the route does; the editor overlay renders in-tree rather
 * than portaling to <body> for exactly this reason).
 */
type RunView = "activity" | "canvas" | "changes" | "trace";

export function RunDetailView({ runId, nested = false, depth = 0, onOpenRun, defaultView = "activity", selectedPhaseId, selectedAgentRunId, onSelectAgent }: { runId: string; nested?: boolean; depth?: number; onOpenRun?: (runId: string) => void; defaultView?: RunView; selectedPhaseId?: string | null; selectedAgentRunId?: string | null; onSelectAgent?: (agentRunId: string | null) => void }) {
  const run = useWorkflowRun(runId);
  // The canvas renders the run's OWN version-pinned definition snapshot (run.definition) — never the
  // workflow's current graph — so it stays faithful to how the run actually ran. Manifests drive the
  // node icons/kinds for definitionToRfNodes.
  const manifests = useNodeManifests();
  const manifestByType = useMemo(() => new Map((manifests.data ?? []).map((m) => [m.typeKey, m])), [manifests.data]);
  const [view, setView] = useState<RunView>(defaultView);
  // The Live-work center is driven by the phase projection (shared ['run-phases', id] cache). Only the framed
  // route needs it — the embedded (nested) dialog keeps the plain node narrative, so it skips the fetch.
  const phases = useRunPhases(nested ? null : runId);

  if (run.isLoading) {
    return <div className="ct-empty"><div className="ct-empty-h">Loading run…</div></div>;
  }

  if (run.error instanceof ApiError || !run.data) {
    return (
      <div className="cn-banner cn-banner-err" style={{ margin: 0 }}>
        <div className="cn-banner-h">Run not found</div>
        <div className="cn-banner-p">{run.error instanceof ApiError ? run.error.message : "It may have been removed."}</div>
      </div>
    );
  }

  const r = run.data;
  // Nodes whose execution overlapped in time — the engine ran them in a parallel wave (top-level or
  // inside a loop body). Badged in the trace so a concurrent run is legible at a glance.
  const concurrent = concurrentNodeKeys(r.nodes);
  // Per-map element-branch rollups parsed from the engine iteration keys. Empty for a non-map run
  // (every row has an empty iteration key) — so a non-map run renders exactly as before.
  const mapRollups = groupMapBranches(r.nodes);

  // Whether the run has any agents decides the layout below: an agent / supervisor run folds the raw node trace away
  // (the Activity timeline is the heart), a structural workflow with no agents keeps the node trace primary.
  const phaseList = phases.data?.phases ?? [];
  const agents = dedupRunAgents(phaseList);

  const payloadBlock = (
    <>
      <section className="wf-section">
        <h2 className="wf-section-h">Normalized payload</h2>
        <JsonView data={r.normalizedPayload} />
      </section>
      {hasContent(r.outputs) && (
        <section className="wf-section">
          <h2 className="wf-section-h">Outputs</h2>
          <JsonView data={r.outputs} />
        </section>
      )}
    </>
  );

  const nodeBlock = (
    <section className="wf-section">
      <h2 className="wf-section-h">Node execution</h2>
      {mapRollups.length > 0 && <MapRollups rollups={mapRollups} />}
      {r.nodes.length === 0 ? (
        <div className="ct-empty">
          <div className="ct-empty-h">No nodes executed yet</div>
          <div className="ct-empty-p">The engine hasn't picked up this run from the outbox yet — refresh in a moment.</div>
        </div>
      ) : (
        <ol className="wf-run-nodes">
          {r.nodes.map((n) => (
            <RunNodeRow
              key={`${n.nodeId}:${n.iterationKey}`}
              node={n}
              branch={branchBadge(n)}
              parallel={concurrent.has(runNodeKey(n))}
              suppressChildEmbed={n.childRunId === r.pendingWait?.token}
              depth={depth}
              onOpenRun={onOpenRun}
            />
          ))}
        </ol>
      )}
    </section>
  );

  return (
    <div className={nested ? "wf-detail-body wf-detail-body-nested" : "wf-detail-body"}>
      {/* Non-nested: the tab strip IS the panel head, so its top edge lines up with the left/right rail-card heads
          (the status·source·version·date the summary line used to show is already in the page header + the Run
          rail, so it's dropped here to fix the center-vs-rails misalignment). Nested (the editor dialog) has no
          header/rails, so it keeps the compact summary line. */}
      {nested ? (
        <div className="wf-run-summary">
          <RunStatusBadge status={r.status} />
          <span>·</span>
          <span className="wf-trigger-chip wf-trigger-chip-soft">{r.sourceType}</span>
          <span>·</span>
          <span className="wf-version">v{r.workflowVersion}</span>
          {r.startedAt && (
            <>
              <span>·</span>
              <span>{new Date(r.startedAt).toLocaleString()}</span>
            </>
          )}
        </div>
      ) : (
        <div className="wf-run-views wf-run-views-inline" role="tablist" aria-label="Run view">
          <button type="button" role="tab" aria-selected={view === "activity"} data-active={view === "activity"} onClick={() => setView("activity")}>
            <Ic.Clock size={13} /> Activity
          </button>
          <button type="button" role="tab" aria-selected={view === "canvas"} data-active={view === "canvas"} onClick={() => setView("canvas")}>
            <Ic.Workflow size={13} /> Canvas
          </button>
          <button type="button" role="tab" aria-selected={view === "changes"} data-active={view === "changes"} onClick={() => setView("changes")}>
            <Ic.Branch size={13} /> Changes
          </button>
          <button type="button" role="tab" aria-selected={view === "trace"} data-active={view === "trace"} onClick={() => setView("trace")}>
            <Ic.Code size={13} /> Trace
          </button>
        </div>
      )}

      {r.error && (
        <div className="cn-banner cn-banner-err" style={{ margin: 0 }}>
          <div className="cn-banner-h">Run failed</div>
          <div className="cn-banner-p" style={{ fontFamily: "inherit" }}>{r.error}</div>
        </div>
      )}

      {r.status === "Suspended" && r.pendingWait && (
        <SuspendedPanel runId={runId} wait={r.pendingWait} depth={depth} onOpenRun={onOpenRun} />
      )}

      {!nested && view === "canvas" ? (
        r.definition
          ? <RunCanvas definition={r.definition} runNodes={r.nodes} runStatus={r.status} manifestByType={manifestByType} onOpenRun={onOpenRun} />
          : <div className="wf-run-canvas wf-run-canvas-loading">This run's graph snapshot isn't available.</div>
      ) : !nested && view === "changes" ? (
        <RunTabComingSoon title="Changes"
          note="The files this run created or modified — per-repo change sets, diffs, and the pull requests it opened." />
      ) : !nested && view === "trace" ? (
        <RunTrace runId={runId} />
      ) : (
        <>
          {/* Activity — the run's agents as a flat grid of live terminal tiles, driven by the outline (a selected phase
              filters them; a selected agent opens its terminal). The run's narrative + raw audit live in the Trace tab. */}
          {!nested && <RunActivityTiles runId={runId} selectedPhaseId={selectedPhaseId} selectedAgentRunId={selectedAgentRunId} onSelectAgent={onSelectAgent} />}

          {nested || agents.length === 0 ? (
            // The editor dialog, or a structural workflow with no agents: the node trace IS the content.
            <>
              {payloadBlock}
              {nodeBlock}
            </>
          ) : (
            // An agent / supervisor run: the Activity timeline above is the heart, so the raw input/output + node
            // trace fold away (the full raw stream lives in the Trace tab).
            <>
              <Fold title="Run input & output">{payloadBlock}</Fold>
              <Fold title="Workflow nodes">{nodeBlock}</Fold>
            </>
          )}
        </>
      )}
    </div>
  );
}

/**
 * A collapsed disclosure for the raw detail an agent/supervisor run pushes below its Live-work cards (the run's
 * input/output JSON, the node trace). Lazy: the body only mounts on expand, so a closed fold costs no polling
 * from the agent timelines nested inside the node trace.
 */
function Fold({ title, children }: { title: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);

  return (
    <details className="run-fold" onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="run-fold-summary">{title}</summary>
      {open && <div className="run-fold-body">{children}</div>}
    </details>
  );
}

/**
 * One node row in the execution trace. Extracted from the trace map so it can observe its agent run's
 * LIVE status (via {@link useAgentRun}) and badge accordingly — a hook can't run inside a `.map`.
 */
function RunNodeRow({ node: n, branch, parallel, suppressChildEmbed, depth, onOpenRun }: {
  node: WorkflowRunNodeSummary;
  /** Map-branch badge for this row ("#i", or "#i/#j" nested); "" for a non-map node. */
  branch: string;
  parallel: boolean;
  suppressChildEmbed: boolean;
  depth: number;
  onOpenRun?: (runId: string) => void;
}) {
  // Shares AgentRunTimeline's query (same key) so the badge can reflect the agent's live status while the
  // node is parked — React Query dedupes by agentRunId, so no extra fetch; disabled (no fetch) for a
  // non-agent node, where agentRunId is null.
  const agentRun = useAgentRun(n.agentRunId ?? undefined);
  const parked = isParkedOnLiveAgent(n, agentRun.data?.status);

  return (
    <li className="wf-run-node">
      <div className="wf-run-node-head">
        {/* A flow.subworkflow step carries the child run it spawned: click the id to open
            that run full-page, or expand the card below to inspect it inline. */}
        {n.childRunId && onOpenRun ? (
          <button type="button" className="wf-run-node-id wf-run-node-link" onClick={() => onOpenRun(n.childRunId!)} title="Open the sub-workflow run">
            {n.nodeId}
            <Ic.ArrowOut size={11} />
          </button>
        ) : (
          <span className="wf-run-node-id">{n.nodeId}</span>
        )}
        {/* Element-branch badge — which map element this row belongs to (#i, or #i/#j for a nested
            map-in-map). Lets K identical body-node rows read as K distinct branches at a glance. */}
        {branch && (
          <span className="wf-run-node-branch" title="Map element branch (index per map level)">{branch}</span>
        )}
        {/* For an agent.code node, the raw node status is "Suspended" the whole time the agent is actually
            working (the node parks on its AgentRun wait). Surface the agent run's live status instead so the
            row reads as active work, not an idle wait; the engine truth stays on hover. */}
        {parked
          ? <RunStatusBadge status={agentRun.data!.status} title="Workflow node is parked (Suspended) while its agent runs" />
          : <RunStatusBadge status={n.status} />}
        {parallel && (
          <span className="wf-run-node-parallel" title="Ran concurrently with another node (parallel wave)">∥ parallel</span>
        )}
        {n.startedAt && (
          <span className="wf-run-node-time">
            {new Date(n.startedAt).toLocaleTimeString()}
            {n.completedAt && ` → ${new Date(n.completedAt).toLocaleTimeString()}`}
          </span>
        )}
      </div>
      {n.error && <pre className="wf-json wf-json-err">{n.error}</pre>}
      {/* A trigger node consumes nothing (Inputs is genuinely {}), so hide the empty
          block — the entered values surface under Outputs + NORMALIZED PAYLOAD. */}
      {hasContent(n.inputs) && (
        <details className="wf-run-node-io">
          <summary>Inputs</summary>
          <JsonView data={n.inputs} />
        </details>
      )}
      {hasContent(n.outputs) && (
        <details className="wf-run-node-io">
          <summary>Outputs</summary>
          <JsonView data={n.outputs} />
        </details>
      )}
      {/* The child run for a sub-workflow step — a peer disclosure of Inputs/Outputs (same
          marker + indent), collapsed by default so N steps cost no extra polling until
          expanded; the embedded view brings its own resume affordance. Suppressed for the
          node the run is *currently* suspended on: the SuspendedPanel above already embeds
          that same child (open), so showing it here too would double up + double-poll. */}
      {n.childRunId && !suppressChildEmbed && (
        <SubworkflowRunDisclosure childRunId={n.childRunId} depth={depth} onOpenRun={onOpenRun} />
      )}
      {/* An agent.code step: stream its run's live status + event timeline inline, so you watch
          the agent work in real time (and see WHY, not just a static "Suspended"/final status). */}
      {n.agentRunId && <AgentRunTimeline agentRunId={n.agentRunId} />}
      {/* …and, alongside the narrative timeline, the GOVERNED tool-call audit: every side-effecting
          MCP call the agent made, its outcome, and the approval trail — so an operator can see WHAT
          the agent did, not just what it said. */}
      {n.agentRunId && <AgentToolCalls agentRunId={n.agentRunId} />}
      {!n.error && !hasContent(n.inputs) && !hasContent(n.outputs) && !n.childRunId && !n.agentRunId && (
        <div className="wf-run-node-none">No inputs or outputs recorded.</div>
      )}
    </li>
  );
}

/**
 * Per-map element-branch rollup — one chip per flow.map (or per OUTER-pass of a nested map), showing
 * how its fan-out is going: total elements observed, how many finished cleanly (`done`), how many failed.
 * `done` counts ONLY fully-settled clean branches — a branch with a still-running / suspended row sits in
 * `total` but neither `done` nor `failed`, so a live map reads e.g. "1/3 done" while two branches run, not
 * a misleading "3/3". Once the run completes this matches the map node's own `count` / `failed` outputs.
 */
function MapRollups({ rollups }: { rollups: MapRollup[] }) {
  return (
    <div className="wf-map-rollups">
      {rollups.map((m) => (
        <div key={`${m.mapId}:${m.branchIndices.join(",")}`} className="wf-map-rollup" title={`Map "${m.mapId}" fanned out ${m.total} element-branch(es)`}>
          <Ic.Fork size={12} />
          <span className="wf-map-rollup-id">{m.mapId}</span>
          <span className="wf-map-rollup-stat">{m.done}/{m.total} done</span>
          {m.failed > 0 && <span className="wf-map-rollup-failed">{m.failed} failed</span>}
        </div>
      ))}
    </div>
  );
}

/**
 * True when a node is parked (Suspended) on an agent run that is still actively working (Queued/Running) —
 * the case where we badge the row with the agent's live status instead of the bare "Suspended". A terminal
 * agent status means the node is about to resume, so we keep the node's own status until it does.
 */
function isParkedOnLiveAgent(n: WorkflowRunNodeSummary, agentStatus: AgentRunStatus | undefined): boolean {
  return n.status === "Suspended" && !!n.agentRunId && isAgentRunActive(agentStatus);
}

/**
 * An honest placeholder for a run-view tab whose backing projector hasn't shipped yet (Changes, Trace). Keeps the
 * tab visible so the run-detail structure reads as the intended whole, while being explicit that the data is coming
 * — never a fake-empty panel that looks like the run produced nothing.
 */
function RunTabComingSoon({ title, note }: { title: string; note: string }) {
  return (
    <div className="ct-empty run-tab-soon">
      <span className="run-tab-soon-tag">Coming soon</span>
      <div className="ct-empty-h">{title}</div>
      <div className="ct-empty-p">{note}</div>
    </div>
  );
}

/** True when a value is worth a dedicated block — non-null, and not an empty object. */
function hasContent(value: unknown): boolean {
  if (value === null || value === undefined) return false;
  if (typeof value === "object" && !Array.isArray(value)) return Object.keys(value).length > 0;
  return true;
}

/**
 * The resume affordance for a Suspended run. An Approval wait gets approve/reject + an optional
 * comment (posts the decision, then the live poll shows the run continue). A Timer wait just
 * shows when it'll wake. A Callback wait shows the tokened URL. A Subworkflow wait embeds the live
 * child run inline — including ITS resume affordance, so e.g. an approval deep inside the child is
 * operable right here; resolving it completes the child, which auto-resumes this run.
 */
export function SuspendedPanel({ runId, wait, depth = 0, onOpenRun }: { runId: string; wait: WorkflowRunWaitInfo; depth?: number; onOpenRun?: (runId: string) => void }) {
  const resume = useResumeRun(runId);
  const [comment, setComment] = useState("");

  if (wait.kind === "Subworkflow") {
    // The wait's token is the child run id (engine contract). Embed the child run-detail.
    return <SubworkflowWaitPanel childRunId={wait.token} depth={depth} onOpenRun={onOpenRun} />;
  }

  if (wait.kind === "Approval") {
    const prompt = readPrompt(wait.payload);
    const decide = (approved: boolean) => resume.mutate({ approved, comment: comment.trim() || undefined });

    return (
      <section className="wf-section wf-approval">
        <h2 className="wf-section-h">Waiting for approval</h2>
        {prompt && <div className="wf-approval-prompt">{prompt}</div>}
        <textarea
          className="wf-form-input wf-approval-comment"
          rows={2}
          placeholder="Comment (optional)"
          value={comment}
          onChange={(e) => setComment(e.target.value)}
          disabled={resume.isPending}
        />
        <div className="wf-approval-actions">
          <button className="btn btn-primary" onClick={() => decide(true)} disabled={resume.isPending}>Approve</button>
          <button className="btn" onClick={() => decide(false)} disabled={resume.isPending}>Reject</button>
        </div>
        {resume.isError && <div className="wf-approval-err">Couldn&apos;t submit — try again.</div>}
      </section>
    );
  }

  if (wait.kind === "Timer") {
    return (
      <section className="wf-section wf-approval">
        <h2 className="wf-section-h">Sleeping</h2>
        <div className="wf-approval-prompt">
          {wait.wakeAt ? `Resumes around ${new Date(wait.wakeAt).toLocaleTimeString()}.` : "Waiting on a timer."}
        </div>
      </section>
    );
  }

  if (wait.kind === "Callback") {
    const url = `${window.location.origin}/api/workflows/callbacks/${wait.token}`;
    return (
      <section className="wf-section wf-approval">
        <h2 className="wf-section-h">Waiting for callback</h2>
        <div className="wf-approval-prompt">An external system resumes this run by POSTing to:</div>
        <div className="wf-callback-row">
          <input className="wf-form-input wf-callback-url" readOnly value={url} onFocus={(e) => e.currentTarget.select()} />
          <button className="btn" onClick={() => navigator.clipboard?.writeText(url)}>Copy</button>
        </div>
      </section>
    );
  }

  return null;
}

/**
 * The Subworkflow-wait affordance shown at the top of a Suspended run: the child run embedded as the
 * action surface (open by default), so an approval / callback inside the child is operated right here
 * — resolving it completes the child, which the engine's completion hook turns into a resume of THIS
 * run. Distinct from the trace-row disclosure: this is a prominent, default-open action panel.
 */
function SubworkflowWaitPanel({ childRunId, depth, onOpenRun }: { childRunId: string; depth: number; onOpenRun?: (runId: string) => void }) {
  const [open, setOpen] = useState(true);
  const canEmbed = depth < MAX_EMBED_DEPTH;

  return (
    <section className="wf-section wf-approval">
      <button type="button" className="wf-subrun-toggle" onClick={() => setOpen((v) => !v)} aria-expanded={open} disabled={!canEmbed}>
        {canEmbed && (open ? <Ic.ChevronDown size={12} /> : <Ic.ChevronRight size={12} />)}
        <span className="wf-section-h">Running a sub-workflow</span>
      </button>
      <div className="wf-approval-prompt">
        This run is waiting for the sub-workflow below to finish. Acting on it here — e.g. approving —
        resumes this run automatically.
      </div>
      {open && <EmbeddedChildRun childRunId={childRunId} depth={depth} onOpenRun={onOpenRun} />}
    </section>
  );
}

/**
 * A sub-workflow step's child run in the execution trace, rendered as a peer disclosure of the node's
 * Inputs / Outputs (the same `wf-run-node-io` <details>/<summary>, so the marker, indent and label
 * style line up exactly). The embedded run-detail mounts lazily on expand, so N collapsed steps cost
 * zero extra polling until opened.
 */
function SubworkflowRunDisclosure({ childRunId, depth, onOpenRun }: { childRunId: string; depth: number; onOpenRun?: (runId: string) => void }) {
  const [open, setOpen] = useState(false);

  return (
    <details className="wf-run-node-io" onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary>Sub-workflow run</summary>
      {open && <EmbeddedChildRun childRunId={childRunId} depth={depth} onOpenRun={onOpenRun} />}
    </details>
  );
}

/**
 * The bordered, LIVE child run-detail (recursively a full RunDetailView, so the child brings its own
 * resume affordance). Past MAX_EMBED_DEPTH it stops embedding (each level is a polling fetch) and
 * just names the run id.
 */
function EmbeddedChildRun({ childRunId, depth, onOpenRun }: { childRunId: string; depth: number; onOpenRun?: (runId: string) => void }) {
  if (depth >= MAX_EMBED_DEPTH) {
    return <div className="wf-approval-prompt">Sub-workflow run <code>{childRunId}</code> (nested too deep to embed).</div>;
  }

  return (
    <div className="wf-subrun">
      <RunDetailView runId={childRunId} nested depth={depth + 1} onOpenRun={onOpenRun} />
    </div>
  );
}

/** Pull the approver-facing prompt out of a wait's suspend payload, if present. */
function readPrompt(payload: unknown): string {
  if (payload && typeof payload === "object" && "prompt" in payload) {
    const p = (payload as { prompt?: unknown }).prompt;
    return typeof p === "string" ? p : "";
  }
  return "";
}
