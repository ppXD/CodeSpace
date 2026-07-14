/* eslint-disable react-refresh/only-export-components -- this module co-locates its render component with the small pure helpers (glyphs, formatters, digest/label builders) that only it and its sibling footers use; fast-refresh granularity is moot for these. */
import { useContext, useState, type CSSProperties, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import { formatCountdown, formatElapsed, useNowTick } from "@/hooks/use-now-tick";
import { useNodeLiveContext } from "@/hooks/use-run-live";
import { useResumeRun } from "@/hooks/use-workflows";
import type { NodeLiveSignals } from "@/lib/runLiveFold";

import { RunActionsContext } from "../runActionsContext";
import { aggregateDurationMs, formatDuration, isRowExpandable, ReceiptFooter, resultGlyph, RunRowDetail } from "./ReceiptFooter";
import type { NodeFooterProps } from "./index";

/**
 * The footer for the wait / suspend family (chat.post_message, flow.sleep, flow.decision, flow.wait_action,
 * flow.wait_approval, flow.wait_callback, plan.confirm, flow.subworkflow) — one footer, one wait language,
 * a kind-specific label + glyph. While Suspended it is a CALM bar (no spinner — nothing is computing; the
 * node CARD breathes via the A1 `data-run-status="suspended"` treatment): it reads the live suspend signal
 * (A4) and shows either a depleting countdown ring when the wait has a deadline, or a "parked" elapsed
 * timer. When the node resolves it delegates to the coze {@link ReceiptFooter}, stamping the decision the
 * outputs carry (approved / rejected / chosen option / action key + who). Every payload field is read
 * DEFENSIVELY — a missing datum is omitted, never thrown on.
 */
export function WaitFooter(props: NodeFooterProps) {
  const live = useNodeLiveContext(props.data.nodeId);
  const now = useNowTick();

  if (props.status === "Pending") return null;                 // not reached yet → no footer (matches ReceiptFooter)
  if (props.status === "Suspended") return <SuspendedWait data={props.data} rows={props.rows} wait={live?.wait ?? null} now={now} />;

  return <ResolvedWait {...props} />;
}

type WaitSignal = NonNullable<NodeLiveSignals["wait"]>;
type WaitKind = "approval" | "planConfirm" | "action" | "callback" | "timer" | "decision" | "subworkflow" | "message" | "generic";

/**
 * The canonical wait kind. The node's typeKey is the authoritative classifier for the builtin family (a
 * closed set), so it wins; the live signal's `kind` is the fallback for a plugin node routed here via its
 * category. Deterministic + total — an unheard-of type still resolves to `generic`.
 */
function classifyWaitKind(typeKey: string, liveKind?: string): WaitKind {
  switch (typeKey) {
    case "flow.wait_approval": return "approval";
    case "plan.confirm": return "planConfirm";
    case "flow.wait_action": return "action";
    case "flow.wait_callback": return "callback";
    case "flow.sleep": return "timer";
    case "flow.decision": return "decision";
    case "flow.subworkflow": return "subworkflow";
    case "chat.post_message": return "message";
  }

  const k = (liveKind ?? "").toLowerCase();
  if (k.includes("approv")) return "approval";
  if (k.includes("action")) return "action";
  if (k.includes("callback")) return "callback";
  if (k.includes("timer") || k.includes("sleep")) return "timer";
  if (k.includes("decision") || k.includes("decide")) return "decision";
  if (k.includes("confirm") || k.includes("plan")) return "planConfirm";
  if (k.includes("subworkflow") || k.includes("subflow")) return "subworkflow";

  return "generic";
}

/** The fixed label + glyph for a kind — the headline is overridden per-kind by payload where richer (a decision's question, a plan version). */
function kindPresentation(kind: WaitKind): { label: string; glyph: ReactNode } {
  switch (kind) {
    case "approval": return { label: "Awaiting approval", glyph: <Ic.Bell size={12} /> };
    case "planConfirm": return { label: "Plan pending confirmation", glyph: <Ic.Check size={12} /> };
    case "action": return { label: "Awaiting action", glyph: <Ic.Zap size={12} /> };
    case "callback": return { label: "Awaiting callback", glyph: <Ic.Link size={12} /> };
    case "timer": return { label: "Sleeping", glyph: <Ic.Clock size={12} /> };
    case "decision": return { label: "Awaiting decision", glyph: <Ic.Help size={12} /> };
    case "subworkflow": return { label: "Awaiting subworkflow", glyph: <Ic.ArrowOut size={12} /> };
    case "message": return { label: "Waiting", glyph: <Ic.Bell size={12} /> };
    default: return { label: "Waiting", glyph: <Ic.Pause size={12} /> };
  }
}

/** The bar headline: a decision leads with its question, a plan-confirm with its version; every other kind uses its fixed label. */
function waitBarLabel(kind: WaitKind, base: string, payload: Record<string, unknown> | null): string {
  if (kind === "decision") return (payload && readStr(payload, "question", "prompt")) || base;

  if (kind === "planConfirm") {
    const v = payload ? readVersion(payload) : undefined;
    return v != null ? `Plan pending confirmation v${v}` : base;
  }

  return base;
}

/**
 * The Suspended wait bar: a calm, spinner-less receipt-style bar (`data-status="suspended"`) with the kind's
 * glyph + label + a timing element (deadline → countdown ring; else → parked elapsed), then a kind-specific
 * detail line and, for an approval, inline resume buttons.
 */
function SuspendedWait({ data, rows, wait, now }: { data: NodeFooterProps["data"]; rows: WorkflowRunNodeSummary[]; wait: WaitSignal | null; now: number }) {
  const kind = classifyWaitKind(data.typeKey, wait?.kind);
  const pres = kindPresentation(kind);
  const payload = payloadObj(wait?.payload);
  const label = waitBarLabel(kind, pres.label, payload);

  // A node-title fallback keeps the bar meaningful when there's neither a live signal nor a payload headline.
  const headline = label || data.label || data.displayName;

  return (
    <div className="wf-rf-result wf-wait nodrag nopan" data-status="suspended" data-kind={kind}>
      <div className="wf-rf-result-bar wf-wait-bar">
        <span className="wf-rf-result-glyph" aria-hidden="true">{pres.glyph}</span>
        <span className="wf-rf-result-label">{headline}</span>
        <WaitTiming wait={wait} now={now} payload={payload} />
      </div>

      <WaitDetail kind={kind} payload={payload} wait={wait} rows={rows} />
      <WaitActions kind={kind} typeKey={data.typeKey} />
    </div>
  );
}

/** The timing region: a depleting `.wf-ring` + countdown when the wait has a deadline; otherwise a "parked" elapsed marker. */
function WaitTiming({ wait, now, payload }: { wait: WaitSignal | null; now: number; payload: Record<string, unknown> | null }) {
  if (wait?.deadlineAtMs) {
    const total = Math.max(1, wait.deadlineAtMs - wait.sinceMs);
    const remaining = Math.max(0, wait.deadlineAtMs - now);
    const pct = Math.max(0, Math.min(100, (remaining / total) * 100));
    const onTimeout = payload ? readStr(payload, "defaultAction", "onTimeout", "timeoutAction", "onTimeoutAction", "default") : undefined;

    return (
      <span className="wf-wait-time" title="Countdown to timeout">
        <span className="wf-ring" style={{ "--wf-ring-p": `${pct}%` } as CSSProperties} aria-hidden="true" />
        <span className="wf-wait-count">{formatCountdown(wait.deadlineAtMs, now)}</span>
        {onTimeout && <span className="wf-wait-default">→ {onTimeout}</span>}
      </span>
    );
  }

  return (
    <span className="wf-wait-time">
      <span className="wf-wait-parked">Parked{wait ? ` ${formatElapsed(now - wait.sinceMs)}` : ""}</span>
    </span>
  );
}

/** The kind-specific detail below the bar: an approval prompt, an action key, a copyable callback URL, or a timer wake time. */
function WaitDetail({ kind, payload, wait, rows }: { kind: WaitKind; payload: Record<string, unknown> | null; wait: WaitSignal | null; rows: WorkflowRunNodeSummary[] }) {
  if (kind === "approval" || kind === "planConfirm") {
    const prompt = payload ? readStr(payload, "prompt", "message", "question") : undefined;
    return prompt ? <div className="wf-wait-detail">{prompt}</div> : null;
  }

  if (kind === "action") {
    const action = payload ? readStr(payload, "action", "actionKey", "actionKind") : undefined;
    return action ? <div className="wf-wait-detail">Action key <code>{action}</code></div> : null;
  }

  if (kind === "callback") {
    const url = callbackUrl(payload);
    return url ? <CallbackRow url={url} /> : null;
  }

  if (kind === "timer") {
    if (wait?.deadlineAtMs) return <div className="wf-wait-detail">Wakes {new Date(wait.deadlineAtMs).toLocaleTimeString()}</div>;
    return null;
  }

  if (kind === "subworkflow") {
    const childRunId = rows[0]?.childRunId;
    return childRunId ? <div className="wf-wait-detail">Subworkflow <code>{childRunId}</code></div> : null;
  }

  return null;
}

/** A read-only callback URL with a Copy button — the external system POSTs here to resume the run. Mirrors SuspendedPanel's callback row. */
function CallbackRow({ url }: { url: string }) {
  const [copied, setCopied] = useState(false);
  const copy = () => { void navigator.clipboard?.writeText(url); setCopied(true); };

  return (
    <div className="wf-wait-cb">
      <input className="wf-wait-cb-url" readOnly value={url} onFocus={(e) => e.currentTarget.select()} />
      <button type="button" className="wf-wait-cb-copy" onClick={copy}><Ic.Copy size={11} /> {copied ? "Copied" : "Copy"}</button>
    </div>
  );
}

/**
 * The Suspended action affordance. Inline approve / reject is wired ONLY for flow.wait_approval — reusing the
 * SAME resume path SuspendedPanel drives ({@link useResumeRun}); the other interactive kinds (decision, plan
 * confirm, wait_action) point to the run-detail surface for now. TODO(B5-followup): wire those inline too.
 */
function WaitActions({ kind, typeKey }: { kind: WaitKind; typeKey: string }) {
  const actions = useContext(RunActionsContext);

  if (typeKey === "flow.wait_approval" && actions?.runId) return <ApprovalActions runId={actions.runId} />;

  if (kind === "decision" || kind === "action" || kind === "planConfirm" || kind === "approval") {
    return <div className="wf-wait-hint">Respond in the run detail</div>;
  }

  return null;
}

/**
 * Inline approve / reject buttons reusing the run's resume mutation. Optimistic: a click flips to "Submitted…"
 * immediately; on error it rolls back to the buttons and surfaces the failure inline (no global toast system
 * exists — SuspendedPanel likewise shows an inline error). On success the run leaves Suspended and the poll
 * re-renders this node out of the wait state.
 */
function ApprovalActions({ runId }: { runId: string }) {
  const resume = useResumeRun(runId);
  const [submitted, setSubmitted] = useState(false);

  const decide = (approved: boolean) => {
    setSubmitted(true);
    resume.mutate({ approved, comment: undefined }, { onError: () => setSubmitted(false) });
  };

  if (submitted) return <div className="wf-wait-sent">Submitted…</div>;

  return (
    <div className="wf-wait-approve">
      <button type="button" className="wf-wait-btn wf-wait-btn-ok" onClick={() => decide(true)} disabled={resume.isPending}>Approve</button>
      <button type="button" className="wf-wait-btn wf-wait-btn-no" onClick={() => decide(false)} disabled={resume.isPending}>Reject</button>
      {resume.isError && <span className="wf-wait-err" role="alert">Submit failed — please retry</span>}
    </div>
  );
}

/**
 * The resolved / terminal branch: delegate to the coze {@link ReceiptFooter} for the bar + expand panel, but
 * when a single row carries a decision, stamp it (approved / rejected / chosen option / action key + who).
 */
function ResolvedWait(props: NodeFooterProps) {
  const resolution = props.rows.length === 1 ? waitResolution(props.data.typeKey, props.rows) : null;
  if (!resolution) return <ReceiptFooter {...props} />;

  return <ResolvedWaitBar row={props.rows[0]} status={props.status} resolution={resolution} title={props.title} />;
}

/** A resolved wait bar — the shared `.wf-rf-result*` bar + `RunRowDetail` expand panel, with the decision stamped into the header. */
function ResolvedWaitBar({ row, status, resolution, title }: { row: WorkflowRunNodeSummary; status: NodeStatus; resolution: WaitResolution; title?: string }) {
  const [open, setOpen] = useState(false);
  const durationMs = aggregateDurationMs([row]);
  const expandable = isRowExpandable(row);
  const rich = !!row.agentRunId || !!row.childRunId;

  return (
    <div className="wf-rf-result wf-wait-resolved nodrag nopan" data-status={status.toLowerCase()} data-open={open || undefined}>
      <button
        type="button"
        className="wf-rf-result-bar"
        data-expandable={expandable || undefined}
        aria-expanded={expandable ? open : undefined}
        title={title}
        onClick={(e) => { e.stopPropagation(); if (expandable) setOpen((v) => !v); }}
      >
        <span className="wf-rf-result-glyph" aria-hidden="true">{resultGlyph(status)}</span>
        <span className="wf-rf-result-label">{status}</span>
        <span className="wf-wait-stamp" data-tone={resolution.tone}>{resolution.label}</span>
        {durationMs != null && <span className="wf-rf-result-dur">{formatDuration(durationMs)}</span>}
        {expandable && <span className="wf-rf-result-caret" aria-hidden="true"><Ic.ChevronDown size={12} /></span>}
      </button>
      {open && expandable && (
        <div className="wf-rf-result-panel nowheel nodrag" data-rich={rich || undefined} onClick={(e) => e.stopPropagation()}>
          <RunRowDetail row={row} />
        </div>
      )}
    </div>
  );
}

export interface WaitResolution {
  label: string;
  tone: "ok" | "reject" | "neutral";
}

/**
 * The decision a resolved wait row carries, read DEFENSIVELY off `rows[0].outputs`: an approval / plan-confirm
 * → approved / rejected (+ who); a decision → the chosen option (+ who); a wait_action → the action key (+ who).
 * Returns null when the node isn't a decision-bearing kind, or the outputs carry no recognisable resolution.
 */
export function waitResolution(typeKey: string, rows: WorkflowRunNodeSummary[]): WaitResolution | null {
  const out = rows[0]?.outputs;
  if (!out || typeof out !== "object" || Array.isArray(out)) return null;

  const o = out as Record<string, unknown>;
  const kind = classifyWaitKind(typeKey);

  if (kind === "approval" || kind === "planConfirm") {
    const approved = readBool(o, "approved") ?? decisionApproved(o);
    if (approved === undefined) return null;

    const who = readStr(o, "approvedBy", "answeredBy", "decidedBy", "by");
    const suffix = who ? ` · ${who}` : "";
    return approved ? { label: `✓ Approved${suffix}`, tone: "ok" } : { label: `✗ Rejected${suffix}`, tone: "reject" };
  }

  if (kind === "decision") {
    const choice = readStr(o, "selectedOption", "choice", "option", "answer", "decision");
    if (!choice) return null;

    const who = readStr(o, "answeredBy", "decidedBy", "by");
    return { label: `Chose ${choice}${who ? ` · ${who}` : ""}`, tone: "ok" };
  }

  if (kind === "action") {
    const action = readStr(o, "action", "actionKey", "actionKind");
    if (!action) return null;

    const who = readStr(o, "responder", "respondedBy", "by");
    return { label: `Action ${action}${who ? ` · ${who}` : ""}`, tone: "ok" };
  }

  return null;
}

/** Narrow an unknown suspend payload to a plain object; null for anything else — so every field read stays guarded. */
function payloadObj(payload: unknown): Record<string, unknown> | null {
  return payload && typeof payload === "object" && !Array.isArray(payload) ? (payload as Record<string, unknown>) : null;
}

/** The callback URL from a payload — a direct URL field wins; else build the tokened resume URL. Null when neither is present. */
function callbackUrl(payload: Record<string, unknown> | null): string | null {
  if (!payload) return null;

  const direct = readStr(payload, "url", "callbackUrl", "callback_url");
  if (direct) return direct;

  const token = readStr(payload, "token", "callbackToken", "callback_token");
  if (token && typeof window !== "undefined") return `${window.location.origin}/api/workflows/callbacks/${token}`;

  return null;
}

/** A string decision expressed as a word ("approved" / "rejected" / …); undefined when the field is absent or unrecognised. */
function decisionApproved(o: Record<string, unknown>): boolean | undefined {
  const d = readStr(o, "decision", "outcome", "result")?.toLowerCase();
  if (d === "approved" || d === "approve" || d === "accepted" || d === "confirmed") return true;
  if (d === "rejected" || d === "reject" || d === "declined" || d === "denied") return false;
  return undefined;
}

/** The first non-empty string among `keys`; undefined when none is a usable string. */
function readStr(o: Record<string, unknown>, ...keys: string[]): string | undefined {
  for (const k of keys) {
    const v = o[k];
    if (typeof v === "string" && v.trim() !== "") return v;
  }
  return undefined;
}

/** A boolean field; undefined when absent or non-boolean. */
function readBool(o: Record<string, unknown>, key: string): boolean | undefined {
  const v = o[key];
  return typeof v === "boolean" ? v : undefined;
}

/** A plan version number from the payload (number, or an all-digits string); undefined when none parses. */
function readVersion(o: Record<string, unknown>): number | undefined {
  for (const k of ["version", "planVersion", "v", "revision"]) {
    const v = o[k];
    if (typeof v === "number" && Number.isFinite(v)) return v;
    if (typeof v === "string" && /^\d+$/.test(v)) return Number(v);
  }
  return undefined;
}