/* eslint-disable react-refresh/only-export-components -- this module co-locates its render component with the small pure helpers (glyphs, formatters, digest/label builders) that only it and its sibling footers use; fast-refresh granularity is moot for these. */
import type { CSSProperties, ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunNodeSummary } from "@/api/workflows";
import { useNodeLiveContext } from "@/hooks/use-run-live";
import { formatElapsed, useNowTick } from "@/hooks/use-now-tick";

import type { WorkflowNodeData } from "../WorkflowNode";
import { ReceiptFooter } from "./ReceiptFooter";
import type { NodeFooterProps } from "./index";

/**
 * The footer for the 10 single-call git nodes + `http.request` (every typeKey the registry resolves to the
 * `externalCall` kind). It reads the same coze-style `.wf-rf-result` language as {@link ReceiptFooter} but adds
 * two things the plain receipt can't say about a network call: WHILE RUNNING it paints the live external-call
 * span (verb + shortened target + a ticking elapsed, and a depletion ring for the timeout-bounded `http.request`)
 * off the {@link useNodeLiveContext} live-signal store; ONCE TERMINAL it replaces the generic "Success · 1.2s"
 * label with a per-type receipt digest ({@link digestExternalCall}) — "#42 opened", "merged · a1b2c3d", "5/5 ·
 * success" — while reusing ReceiptFooter's exact bar + expand panel for everything else. Degrades cleanly: no
 * live store (editor / no run) → renders from `rows` alone.
 */
export function ExternalCallFooter(props: NodeFooterProps) {
  const live = useNodeLiveContext(props.data.nodeId);
  const now = useNowTick();

  if (props.status === "Running") return <RunningBar data={props.data} title={props.title} live={live?.call ?? null} now={now} />;

  const digest = digestExternalCall(props.data.typeKey, props.rows);

  if (!digest) return <ReceiptFooter {...props} />;

  return <ReceiptFooter {...props} labelSlot={<span className="wf-rf-digest" data-tone={digest.tone}>{digest.label}</span>} />;
}

/** The live external-call span while a node is Running: verb + short target + a ticking elapsed, plus a timeout ring for http.request. */
function RunningBar({ data, title, live, now }: { data: WorkflowNodeData; title?: string; live: LiveCall | null; now: number }) {
  const label = live ? runningLabel(live) : (title ?? data.displayName ?? data.label ?? "Calling…");
  const elapsedMs = live ? Math.max(0, now - live.startedAtMs) : null;
  const ringPercent = live ? timeoutRingPercent(data, elapsedMs ?? 0) : null;

  return (
    <div className="wf-rf-result nodrag nopan" data-status="running">
      <div className="wf-rf-result-bar">
        <span className="wf-rf-result-glyph" aria-hidden="true"><span className="wf-rf-status-spin" /></span>
        <span className="wf-rf-result-label">{label}</span>
        {ringPercent != null && <span className="wf-ring wf-rf-result-ring" aria-hidden="true" style={{ "--wf-ring-p": `${ringPercent}%` } as CSSProperties} />}
        {elapsedMs != null && <span className="wf-rf-result-dur">{formatElapsed(elapsedMs)}</span>}
      </div>
    </div>
  );
}

/** The open external-call span shape (from `NodeLiveSignals.call`). */
type LiveCall = { target: string; method: string; startedAtMs: number };

/** "GET api.github.com…" — the call's method + a shortened target; drops an empty method / target so it never shows stray whitespace. */
function runningLabel(call: LiveCall): string {
  const target = shortenTarget(call.target);
  const parts = [call.method.trim(), target].filter((p) => p.length > 0);
  return parts.length > 0 ? `${parts.join(" ")}…` : "Calling…";
}

/** A network target trimmed for the bar: a URL collapses to its host (+ short path); a bare string is truncated. */
function shortenTarget(target: string): string {
  const trimmed = target.trim();
  if (trimmed.length === 0) return "";

  try {
    const url = new URL(trimmed);
    const path = url.pathname.length > 12 ? `${url.pathname.slice(0, 12)}…` : url.pathname === "/" ? "" : url.pathname;
    return `${url.host}${path}`;
  } catch {
    return trimmed.length > 40 ? `${trimmed.slice(0, 40)}…` : trimmed;
  }
}

/** The timeout ring's fill % for http.request (elapsed ÷ configured timeout); null for every other type or when no timeout is known (indeterminate → no ring). */
function timeoutRingPercent(data: WorkflowNodeData, elapsedMs: number): number | null {
  if (data.typeKey !== "http.request") return null;

  const timeoutSeconds = readTimeoutSeconds(data);
  if (timeoutSeconds == null || timeoutSeconds <= 0) return null;

  return Math.min(100, Math.max(0, (elapsedMs / (timeoutSeconds * 1000)) * 100));
}

/** Read the http.request timeout (seconds) from node config if the run canvas carried it; undefined when absent (the card data omits config today, so the ring stays off until a PR threads it through). */
function readTimeoutSeconds(data: WorkflowNodeData): number | undefined {
  const config = (data as { config?: unknown }).config;
  if (config && typeof config === "object") {
    const value = (config as Record<string, unknown>)["timeoutSeconds"];
    if (typeof value === "number" && Number.isFinite(value)) return value;
  }

  const flat = (data as Record<string, unknown>)["timeoutSeconds"];
  return typeof flat === "number" && Number.isFinite(flat) ? flat : undefined;
}

/** A per-type terminal digest: the receipt label + the outcome tone that tints it. */
export interface ExternalCallDigest {
  label: ReactNode;
  tone: "success" | "warn" | "failure";
}

/**
 * The pure per-type receipt digest for a settled external call — reads `rows[0].outputs` DEFENSIVELY (every field
 * guarded by `typeof`; a missing datum is skipped, never assumed) and returns the compact "what happened" line plus
 * an outcome tone, or null when the type is unknown / carries no output to summarize (the footer then falls back to
 * the plain status·duration bar). Note http.request's non-2xx is `warn`, NOT `failure`: `ok:false` is branchable
 * data a downstream node routes on, so it must never read as a red error.
 */
export function digestExternalCall(typeKey: string, rows: WorkflowRunNodeSummary[]): ExternalCallDigest | null {
  const out = readOutputs(rows);
  if (!out) return null;

  switch (typeKey) {
    case "git.open_pr":
    case "git.create_issue":
      return openedDigest(out);
    case "git.merge_pr":
      return mergeDigest(out);
    case "git.fetch_pr_checks":
      return checksDigest(out);
    case "git.fetch_pr_diff":
      return diffDigest(out);
    case "git.list_prs":
      return listDigest(out);
    case "git.pr_review":
      return reviewDigest(out);
    case "git.comment_issue":
    case "git.post_pr_comment":
      return commentDigest(out);
    case "git.close_issue":
      return closeDigest(out);
    case "http.request":
      return httpDigest(out);
    default:
      return null;
  }
}

/** git.open_pr / git.create_issue → `#{number} opened ↗` (the arrow links to the created url/webUrl when present). */
function openedDigest(out: Record<string, unknown>): ExternalCallDigest {
  const number = readNumber(out, "number");
  const url = readString(out, "url") ?? readString(out, "webUrl");

  return {
    tone: "success",
    label: (
      <>
        {number != null && <span className="wf-rf-digest-mono">#{number}</span>} opened{url && <DigestLink href={url} />}
      </>
    ),
  };
}

/** git.merge_pr → `merged · {sha7}` (mono) when merged; else the provider reason (warn — an un-merged result isn't an error). */
function mergeDigest(out: Record<string, unknown>): ExternalCallDigest {
  const merged = readBoolean(out, "merged");
  if (merged) {
    const sha = readString(out, "sha");
    return { tone: "success", label: <>merged{sha && <> · <span className="wf-rf-digest-mono">{sha.slice(0, 7)}</span></>}</> };
  }

  const reason = readString(out, "message");
  return { tone: "warn", label: reason ?? "not merged" };
}

/** git.fetch_pr_checks → `{passing}/{total} · {state}`; success when allPassed, failure when any failing, else warn (pending). */
function checksDigest(out: Record<string, unknown>): ExternalCallDigest {
  const passing = readNumber(out, "passing") ?? 0;
  const total = readNumber(out, "total") ?? 0;
  const state = readString(out, "state");
  const allPassed = readBoolean(out, "allPassed");
  const failing = readNumber(out, "failing") ?? 0;

  const tone = allPassed ? "success" : failing > 0 ? "failure" : "warn";

  return { tone, label: `${passing}/${total}${state ? ` · ${state}` : ""}` };
}

/** git.fetch_pr_diff → `{files} files · +{additions} −{deletions}`. */
function diffDigest(out: Record<string, unknown>): ExternalCallDigest {
  const files = readArrayLength(out, "files") ?? 0;
  const additions = readNumber(out, "additions") ?? 0;
  const deletions = readNumber(out, "deletions") ?? 0;

  return { tone: "success", label: `${files} files · +${additions} −${deletions}` };
}

/** git.list_prs → `{count} PRs`; null when neither the count nor the array is present. */
function listDigest(out: Record<string, unknown>): ExternalCallDigest | null {
  const count = readNumber(out, "count") ?? readArrayLength(out, "pullRequests");
  if (count == null) return null;

  return { tone: "success", label: `${count} PRs` };
}

/** git.pr_review → the verdict word; request_changes reads as warn, approve/comment as neutral success. */
function reviewDigest(out: Record<string, unknown>): ExternalCallDigest | null {
  const verdict = readString(out, "verdict");
  if (!verdict) return null;

  return { tone: verdict === "request_changes" ? "warn" : "success", label: verdict };
}

/** git.comment_issue / git.post_pr_comment → `Published` + link when webUrl is present (a GitLab note has none → no link). */
function commentDigest(out: Record<string, unknown>): ExternalCallDigest {
  const url = readString(out, "webUrl");

  return { tone: "success", label: <>Published{url && <DigestLink href={url} />}</> };
}

/** git.close_issue → the resulting state (open → closed); null when absent. */
function closeDigest(out: Record<string, unknown>): ExternalCallDigest | null {
  const state = readString(out, "state");
  if (!state) return null;

  return { tone: "success", label: state };
}

/** http.request → `{status} ok|not ok`; non-2xx is warn (branchable), NEVER failure. Null when no status landed. */
function httpDigest(out: Record<string, unknown>): ExternalCallDigest | null {
  const status = readNumber(out, "status");
  if (status == null) return null;

  const ok = readBoolean(out, "ok");
  return { tone: ok ? "success" : "warn", label: <><span className="wf-rf-digest-mono">{status}</span> {ok ? "ok" : "not ok"}</> };
}

/** A small external-link arrow inside a digest — opens the created resource; stops the click from toggling the receipt bar's expand. */
function DigestLink({ href }: { href: string }) {
  return (
    <a className="wf-rf-digest-link" href={href} target="_blank" rel="noreferrer" onClick={(e) => e.stopPropagation()} aria-label="Open">
      <Ic.ArrowOut size={11} />
    </a>
  );
}

/** rows[0].outputs as a plain object, or null when absent / non-object (an array output isn't a keyed receipt). */
function readOutputs(rows: WorkflowRunNodeSummary[]): Record<string, unknown> | null {
  const outputs = rows[0]?.outputs;
  return outputs && typeof outputs === "object" && !Array.isArray(outputs) ? (outputs as Record<string, unknown>) : null;
}

/** A numeric field, or undefined when missing / non-number. */
function readNumber(out: Record<string, unknown>, key: string): number | undefined {
  const value = out[key];
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/** A string field, or undefined when missing / non-string / empty. */
function readString(out: Record<string, unknown>, key: string): string | undefined {
  const value = out[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/** A boolean field, or undefined when missing / non-boolean. */
function readBoolean(out: Record<string, unknown>, key: string): boolean | undefined {
  const value = out[key];
  return typeof value === "boolean" ? value : undefined;
}

/** The length of an array field, or undefined when the field isn't an array. */
function readArrayLength(out: Record<string, unknown>, key: string): number | undefined {
  const value = out[key];
  return Array.isArray(value) ? value.length : undefined;
}