import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { isAgentRunActive, type AgentRunStatus } from "@/api/agents";
import type { WorkflowRunNodeSummary, WorkflowRunWaitInfo } from "@/api/workflows";
import { ApiError } from "@/api/request";
import { useAgentRun } from "@/hooks/use-agents";
import { useResumeRun, useWorkflowRun } from "@/hooks/use-workflows";

import { AgentRunTimeline } from "./AgentRunTimeline";
import { AgentToolCalls } from "./AgentToolCalls";
import { JsonView } from "./JsonView";
import { concurrentNodeKeys, runNodeKey } from "./runConcurrency";

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
export function RunDetailView({ runId, nested = false, depth = 0, onOpenRun }: { runId: string; nested?: boolean; depth?: number; onOpenRun?: (runId: string) => void }) {
  const run = useWorkflowRun(runId);

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

  return (
    <div className={nested ? "wf-detail-body wf-detail-body-nested" : "wf-detail-body"}>
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

      {r.error && (
        <div className="cn-banner cn-banner-err" style={{ margin: 0 }}>
          <div className="cn-banner-h">Run failed</div>
          <div className="cn-banner-p" style={{ fontFamily: "inherit" }}>{r.error}</div>
        </div>
      )}

      {r.status === "Suspended" && r.pendingWait && (
        <SuspendedPanel runId={runId} wait={r.pendingWait} depth={depth} onOpenRun={onOpenRun} />
      )}

      <section className="wf-section">
        <h2 className="wf-section-h">Normalized payload</h2>
        <JsonView data={r.normalizedPayload} />
      </section>

      {/* The run's declared Outputs (the Terminal's resolved inputs) — what this run produced.
          Only shown once the run reached a successful Terminal. */}
      {hasContent(r.outputs) && (
        <section className="wf-section">
          <h2 className="wf-section-h">Outputs</h2>
          <JsonView data={r.outputs} />
        </section>
      )}

      <section className="wf-section">
        <h2 className="wf-section-h">Node execution</h2>
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
                parallel={concurrent.has(runNodeKey(n))}
                suppressChildEmbed={n.childRunId === r.pendingWait?.token}
                depth={depth}
                onOpenRun={onOpenRun}
              />
            ))}
          </ol>
        )}
      </section>
    </div>
  );
}

/**
 * One node row in the execution trace. Extracted from the trace map so it can observe its agent run's
 * LIVE status (via {@link useAgentRun}) and badge accordingly — a hook can't run inside a `.map`.
 */
function RunNodeRow({ node: n, parallel, suppressChildEmbed, depth, onOpenRun }: {
  node: WorkflowRunNodeSummary;
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
 * True when a node is parked (Suspended) on an agent run that is still actively working (Queued/Running) —
 * the case where we badge the row with the agent's live status instead of the bare "Suspended". A terminal
 * agent status means the node is about to resume, so we keep the node's own status until it does.
 */
function isParkedOnLiveAgent(n: WorkflowRunNodeSummary, agentStatus: AgentRunStatus | undefined): boolean {
  return n.status === "Suspended" && !!n.agentRunId && isAgentRunActive(agentStatus);
}

/** Status pill shared across the run-detail view + run lists. */
export function RunStatusBadge({ status, title }: { status: string; title?: string }) {
  // Enqueued = workflow-run "claimed by dispatcher, waiting for worker pickup"; Queued = an agent run not yet
  // claimed by its worker — both read as a pending-queue tone.
  const tone =
    status === "Success" || status === "Succeeded" ? "ok"
    : status === "Failure" || status === "Failed" || status === "TimedOut" ? "err"
    : status === "Cancelled" || status === "Skipped" ? "muted"
    : status === "Enqueued" || status === "Queued" ? "queued"
    : status === "Suspended" ? "suspended"
    : "running";

  return <span className={`wf-status-pill wf-status-${tone}`} title={title}>{status}</span>;
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
