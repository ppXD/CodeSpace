/* eslint-disable react-refresh/only-export-components -- this module co-locates its render component with the small pure helpers (glyphs, formatters, digest/label builders) that only it and its sibling footers use; fast-refresh granularity is moot for these. */
import { useState, type CSSProperties, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunNodeSummary } from "@/api/workflows";
import { formatElapsed, useNowTick } from "@/hooks/use-now-tick";
import { useNodeLiveContext } from "@/hooks/use-run-live";

import type { WorkflowNodeData } from "../WorkflowNode";
import { aggregateDurationMs, formatDuration, isRowExpandable, ReceiptFooter, RunRowDetail } from "./ReceiptFooter";
import type { NodeFooterProps } from "./index";

/**
 * The footer for the multi-stage PIPELINE family — the two typeKeys the registry resolves to the `pipeline`
 * kind: `git.integrate` (fan-out contributions folded into one reviewable branch) and `agent.run_command`
 * (one sandboxed shell command). Both are nodes whose whole result is a BRANCHABLE OUTCOME, not a red/green
 * pass/fail: a git.integrate that Conflicted and a run_command that exited non-zero are normal states a
 * downstream node routes on — so this footer is built around that honesty and paints those amber, never red.
 *
 *  - `git.integrate` — RUNNING: a static 5-stage rail (clone → anchor → apply → commit → push) with ONE
 *    indeterminate shimmer sweeping the whole strip. The node runs a real staged pipeline but emits NO
 *    per-step ledger records (see {@link IntegrateRunning}), so we deliberately do NOT light stages as they
 *    finish — that would fabricate progress we can't observe. TERMINAL: a three-way outcome off
 *    `outputs.status` — Clean (green + branch chip), Conflicted (AMBER + first conflict + "nothing pushed"
 *    reassurance), Empty (muted) — reusing ReceiptFooter's expand for the full outputs/conflicts.
 *  - `agent.run_command` — RUNNING: a terminal-cursor blink + elapsed, plus a `.wf-ring` timeout gauge when
 *    the node config carries a timeout. TERMINAL: the exit code is the load-bearing fact — 0 green, non-zero
 *    AMBER (branchable, not a failure), TimedOut a clock — plus byte counts and a 📎 when the full output was
 *    preserved as an artifact.
 *
 * Degrades cleanly: no live store (editor / no run) → renders from `rows` alone, never throwing.
 */
export function PipelineFooter(props: NodeFooterProps) {
  const live = useNodeLiveContext(props.data.nodeId);
  const now = useNowTick();

  if (props.status === "Pending") return null;   // not reached yet → no footer (matches ReceiptFooter)

  const isIntegrate = props.data.typeKey === "git.integrate";

  if (props.status === "Running") {
    return isIntegrate
      ? <IntegrateRunning rows={props.rows} now={now} title={props.title} />
      : <RunCommandRunning data={props.data} rows={props.rows} call={live?.call ?? null} now={now} title={props.title} />;
  }

  // Terminal. git.integrate's three-way outcome needs its own bar (the Conflicted reassurance line hangs below
  // the receipt bar); run_command reuses ReceiptFooter's bar + expand with a digest label stamped in (B1's slot).
  if (isIntegrate) return <IntegrateTerminal {...props} />;

  const digest = pipelineDigest(props.data.typeKey, props.rows);

  if (!digest) return <ReceiptFooter {...props} />;

  return <ReceiptFooter {...props} labelSlot={<span className="wf-rf-digest" data-tone={digest.tone}>{digest.label}</span>} />;
}

// ─── git.integrate — running ────────────────────────────────────────────────────────

/** The staged pipeline git.integrate runs on disk, in order. Display-only — NOT a live progress source (see below). */
const PIPELINE_STAGES = ["clone", "anchor", "apply", "commit", "push"] as const;

/**
 * The Running rail for git.integrate: the 5 known stages as a STATIC strip with a single indeterminate shimmer
 * sweeping the whole rail + a ticking elapsed off `rows[0].startedAt`.
 *
 * HONESTY: git.integrate runs a real clone→anchor→apply→commit→push pipeline, but the integrator emits NO
 * per-step ledger records — there is no signal for "apply finished, commit started". So we CANNOT and do not
 * light stages one-by-one; that would fabricate progress we can't observe. The shimmer is a texture ("working"),
 * never a per-stage indicator.
 */
function IntegrateRunning({ rows, now, title }: { rows: WorkflowRunNodeSummary[]; now: number; title?: string }) {
  const startedAt = rows[0]?.startedAt;
  const start = startedAt ? Date.parse(startedAt) : NaN;
  const hasStart = Number.isFinite(start);

  return (
    <div className="wf-rf-result wf-pf wf-pf-int nodrag nopan" data-status="running">
      <div className="wf-rf-result-bar" title={title}>
        <span className="wf-rf-result-glyph" aria-hidden="true"><span className="wf-rf-status-spin" /></span>
        <span className="wf-rf-result-label">整合中</span>
        {hasStart && <span className="wf-rf-result-dur">{formatElapsed(now - start)}</span>}
      </div>
      <div className="wf-pf-rail" aria-hidden="true">
        {PIPELINE_STAGES.map((stage) => <span key={stage} className="wf-pf-stage">{stage}</span>)}
        <span className="wf-pf-sweep" />
      </div>
    </div>
  );
}

// ─── git.integrate — terminal ───────────────────────────────────────────────────────

/** The three-way integration outcome, normalized from `outputs.status`. Partial (forward-compat) reads as the non-clean amber outcome. */
type IntegrateKind = "clean" | "conflicted" | "empty";

/**
 * The terminal git.integrate bar — a bespoke receipt bar (reusing the shared `.wf-rf-result*` bar + duration +
 * `RunRowDetail` expand) whose TONE is driven by the integration OUTCOME, not the node status: git.integrate
 * SUCCEEDS even on a Conflicted result (it's a routable outcome), so a plain node-status green would lie. The
 * Conflicted branch adds a reassurance line below the bar. Degrades to the plain receipt when there's no
 * recognizable outcome to stamp.
 */
function IntegrateTerminal(props: NodeFooterProps) {
  const [open, setOpen] = useState(false);

  const kind = integrateKind(readOutputs(props.rows));
  const digest = pipelineDigest("git.integrate", props.rows);

  if (!kind || !digest) return <ReceiptFooter {...props} />;

  const durationMs = aggregateDurationMs(props.rows);
  const row = props.rows[0];
  const expandable = !!row && isRowExpandable(row);

  return (
    <div className="wf-rf-result wf-pf wf-pf-int nodrag nopan" data-outcome={kind} data-open={open || undefined}>
      <button
        type="button"
        className="wf-rf-result-bar"
        data-expandable={expandable || undefined}
        aria-expanded={expandable ? open : undefined}
        title={props.title}
        onClick={(e) => { e.stopPropagation(); if (expandable) setOpen((v) => !v); }}
      >
        <span className="wf-rf-result-glyph" aria-hidden="true">{integrateGlyph(kind)}</span>
        <span className="wf-rf-result-label wf-rf-digest" data-tone={digest.tone}>{digest.label}</span>
        {durationMs != null && <span className="wf-rf-result-dur">{formatDuration(durationMs)}</span>}
        {expandable && <span className="wf-rf-result-caret" aria-hidden="true"><Ic.ChevronDown size={12} /></span>}
      </button>

      {kind === "conflicted" && <div className="wf-pf-reassure">什麼都沒推 — 代理分支保留</div>}

      {open && expandable && row && (
        <div className="wf-rf-result-panel nowheel nodrag" onClick={(e) => e.stopPropagation()}>
          <RunRowDetail row={row} />
        </div>
      )}
    </div>
  );
}

/** The outcome glyph: a clean check, a fork (branches kept apart) for a conflict, a neutral dot for an empty set. */
function integrateGlyph(kind: IntegrateKind): ReactNode {
  if (kind === "clean") return <Ic.Check size={12} />;
  if (kind === "conflicted") return <Ic.Fork size={12} />;
  return <Ic.Dot size={14} />;
}

/** Normalize `outputs.status` to the three-way outcome; null for an unknown/absent status (the footer degrades to the plain receipt). */
function integrateKind(out: Record<string, unknown> | null): IntegrateKind | null {
  const status = out ? readString(out, "status") : undefined;
  if (status === "Clean") return "clean";
  if (status === "Conflicted" || status === "Partial") return "conflicted";
  if (status === "Empty") return "empty";
  return null;
}

// ─── agent.run_command — running ─────────────────────────────────────────────────────

/** The open external-call span shape (from `NodeLiveSignals.call`) — run_command wraps its shell run in one. */
type LiveCall = { target: string; method: string; startedAtMs: number };

/**
 * The Running bar for run_command: a terminal-cursor blink + a ticking elapsed. Because the command is
 * timeout-bounded, it also shows a `.wf-ring` depletion gauge (elapsed ÷ timeout) WHEN the node config carries
 * a timeout — but the card data omits config today, so the ring simply stays off until a PR threads it through
 * (degrades exactly like B1's http.request ring). Elapsed prefers the live command span's start, falling back
 * to the node row's start so it still ticks without a live store.
 */
function RunCommandRunning({ data, rows, call, now, title }: { data: WorkflowNodeData; rows: WorkflowRunNodeSummary[]; call: LiveCall | null; now: number; title?: string }) {
  const startMs = call?.startedAtMs ?? rowStartMs(rows);
  const elapsedMs = startMs != null ? Math.max(0, now - startMs) : null;
  const ringPercent = elapsedMs != null ? timeoutRingPercent(data, elapsedMs) : null;

  return (
    <div className="wf-rf-result wf-pf wf-pf-cmd nodrag nopan" data-status="running">
      <div className="wf-rf-result-bar" title={title}>
        <span className="wf-rf-result-glyph" aria-hidden="true"><Ic.Command size={12} /></span>
        <span className="wf-rf-result-label">執行中</span>
        <span className="wf-pf-cursor" aria-hidden="true" />
        {ringPercent != null && <span className="wf-ring wf-pf-ring wf-rf-result-ring" aria-hidden="true" style={{ "--wf-ring-p": `${ringPercent}%` } as CSSProperties} />}
        {elapsedMs != null && <span className="wf-rf-result-dur">{formatElapsed(elapsedMs)}</span>}
      </div>
    </div>
  );
}

/** The node row's start in epoch ms, or null when absent — the fallback elapsed anchor when there's no live span. */
function rowStartMs(rows: WorkflowRunNodeSummary[]): number | null {
  const startedAt = rows[0]?.startedAt;
  if (!startedAt) return null;
  const parsed = Date.parse(startedAt);
  return Number.isNaN(parsed) ? null : parsed;
}

/** The timeout ring's fill % (elapsed ÷ configured timeout); null when no timeout is known (indeterminate → no ring). */
function timeoutRingPercent(data: WorkflowNodeData, elapsedMs: number): number | null {
  const timeoutSeconds = readConfigTimeoutSeconds(data);
  if (timeoutSeconds == null || timeoutSeconds <= 0) return null;

  return Math.min(100, Math.max(0, (elapsedMs / (timeoutSeconds * 1000)) * 100));
}

/** Read the run_command timeout (seconds) from node config if the run canvas carried it; undefined when absent (card data omits config today, so the ring stays off until a PR threads it through). */
function readConfigTimeoutSeconds(data: WorkflowNodeData): number | undefined {
  const config = (data as { config?: unknown }).config;
  if (config && typeof config === "object") {
    const value = (config as Record<string, unknown>)["timeoutSeconds"];
    if (typeof value === "number" && Number.isFinite(value)) return value;
  }

  const flat = (data as Record<string, unknown>)["timeoutSeconds"];
  return typeof flat === "number" && Number.isFinite(flat) ? flat : undefined;
}

// ─── The terminal digest (pure) ──────────────────────────────────────────────────────

/** A terminal pipeline digest: the receipt label + the outcome tone that tints it. Mirrors {@link "./ExternalCallFooter".ExternalCallDigest}. */
export interface PipelineDigest {
  label: ReactNode;
  tone: "success" | "warn" | "failure";
}

/**
 * The pure per-type terminal digest for a settled pipeline node — reads `rows[0].outputs` DEFENSIVELY (every
 * field guarded by `typeof`; a missing datum is skipped, never assumed) and returns the compact "what happened"
 * line plus an outcome tone, or null when the type is unknown / carries no recognizable outcome (the footer
 * then falls back to the plain status·duration bar).
 *
 * The tone vocabulary is deliberately the same three as B1: BOTH families' non-happy outcomes are `warn`, never
 * `failure` — a Conflicted integration and a non-zero command exit are branchable results a downstream node
 * routes on, not node errors, so they must never read red.
 */
export function pipelineDigest(typeKey: string, rows: WorkflowRunNodeSummary[]): PipelineDigest | null {
  const out = readOutputs(rows);
  if (!out) return null;

  if (typeKey === "git.integrate") return integrateDigest(out);
  if (typeKey === "agent.run_command") return runCommandDigest(out);

  return null;
}

/** git.integrate → Clean (branch chip + N/N applied, success) · Conflicted (first conflict, warn) · Empty (muted). Null for an unknown status. */
function integrateDigest(out: Record<string, unknown>): PipelineDigest | null {
  const kind = integrateKind(out);
  if (!kind) return null;

  if (kind === "clean") {
    const branch = readString(out, "integratedBranch");
    const applied = readNumber(out, "appliedCount") ?? 0;
    const total = applied + (readArrayLength(out, "conflicts") ?? 0);   // conflicts is empty on Clean → total == applied

    return {
      tone: "success",
      label: (
        <>
          {branch && <span className="wf-pf-branch"><Ic.Branch size={11} />{branch}</span>}
          <span>{applied}/{total} applied</span>
        </>
      ),
    };
  }

  if (kind === "conflicted") {
    const first = firstConflict(out);
    const reason = first?.reason ?? readString(out, "reason");

    return {
      tone: "warn",
      label: (
        <>
          {first ? <span className="wf-pf-conflict-label">{first.label}</span> : <span>衝突</span>}
          {reason && <span className="wf-pf-conflict-reason">— {reason}</span>}
        </>
      ),
    };
  }

  // Empty — a benign no-op (the node succeeded with nothing to do), so it is muted, never a warning tone.
  return { tone: "success", label: <span className="wf-pf-muted">無可整合</span> };
}

/** The first non-applied contribution from `outputs.conflicts[]` — its label + reason; null when the array is absent/empty or the entry has no label. */
function firstConflict(out: Record<string, unknown>): { label: string; reason?: string } | null {
  const conflicts = out["conflicts"];
  if (!Array.isArray(conflicts) || conflicts.length === 0) return null;

  const entry = conflicts[0];
  if (!entry || typeof entry !== "object") return null;

  const o = entry as Record<string, unknown>;
  const label = readString(o, "label");
  if (!label) return null;

  return { label, reason: readString(o, "reason") };
}

/** agent.run_command → the exit code is the load-bearing fact: 0 green, non-zero warn (branchable, NOT failure), TimedOut a clock. Plus byte counts + a 📎 when the full output was preserved. */
function runCommandDigest(out: Record<string, unknown>): PipelineDigest | null {
  const status = readString(out, "status");
  const exitCode = readNumber(out, "exitCode");

  const timedOut = status === "TimedOut" || status === "Stalled";
  if (!timedOut && exitCode === undefined) return null;   // neither an exit code nor a timeout landed → nothing to stamp

  const bytes = bytesLabel(out);
  const clip = hasArtifact(out) ? <span className="wf-pf-clip" title="完整輸出已保存為工件" aria-label="輸出工件">📎</span> : null;

  if (timedOut) {
    return { tone: "warn", label: <><span className="wf-pf-timeout"><Ic.Clock size={11} /> 逾時</span>{bytes}{clip}</> };
  }

  const ok = exitCode === 0;
  return { tone: ok ? "success" : "warn", label: <><span className="wf-rf-digest-mono">exit {exitCode}</span>{bytes}{clip}</> };
}

/** The stdout/stderr byte strip, e.g. ` · out 3.2 kB · err 0 B`; null when neither byte count is present. */
function bytesLabel(out: Record<string, unknown>): ReactNode | null {
  const stdout = readNumber(out, "stdoutBytes");
  const stderr = readNumber(out, "stderrBytes");
  if (stdout === undefined && stderr === undefined) return null;

  return (
    <span className="wf-pf-bytes">
      · out {formatBytes(stdout ?? 0)} · err {formatBytes(stderr ?? 0)}
    </span>
  );
}

/** A byte count in B / kB / MB (1024-based), one decimal past a kilobyte. */
function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} kB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

/** True when the run preserved a full stdout/stderr artifact (the inline output was capped). */
function hasArtifact(out: Record<string, unknown>): boolean {
  return readString(out, "stdoutArtifactId") !== undefined || readString(out, "stderrArtifactId") !== undefined;
}

// ─── Defensive output readers ─────────────────────────────────────────────────────────

/** rows[0].outputs as a plain object, or null when absent / non-object (an array output isn't a keyed receipt). */
function readOutputs(rows: WorkflowRunNodeSummary[]): Record<string, unknown> | null {
  const outputs = rows[0]?.outputs;
  return outputs && typeof outputs === "object" && !Array.isArray(outputs) ? (outputs as Record<string, unknown>) : null;
}

/** A finite numeric field, or undefined when missing / non-number. */
function readNumber(out: Record<string, unknown>, key: string): number | undefined {
  const value = out[key];
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/** A non-empty string field, or undefined when missing / non-string / empty. */
function readString(out: Record<string, unknown>, key: string): string | undefined {
  const value = out[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/** The length of an array field, or undefined when the field isn't an array. */
function readArrayLength(out: Record<string, unknown>, key: string): number | undefined {
  const value = out[key];
  return Array.isArray(value) ? value.length : undefined;
}