/* eslint-disable react-refresh/only-export-components -- this module co-locates its render component with the small pure helpers (glyphs, formatters, digest/label builders) that only it and its sibling footers use; fast-refresh granularity is moot for these. */
import type { ReactNode } from "react";

import type { WorkflowRunNodeSummary } from "@/api/workflows";

import { ReceiptFooter } from "./ReceiptFooter";
import type { NodeFooterProps } from "./index";

/**
 * The footer for the trigger family (every typeKey the registry resolves to `receipt`). It reads the same coze-style
 * `.wf-rf-result` language as {@link ReceiptFooter} but, once the trigger has SUCCEEDED, replaces the generic
 * "Success" label with a per-trigger receipt digest ({@link triggerDigest}) — "#42 Fix the bug", "main · 3 commits ·
 * a1b2c3d", "由 alice" — so the entry card says WHAT fired the run, not just that it fired. Non-trigger receipt-kind
 * nodes (logic containers, flow markers, plugin fallbacks) carry no digest, so this passes them straight through to
 * the plain receipt bar unchanged.
 */
export function TriggerReceiptFooter(props: NodeFooterProps) {
  const digest = props.status === "Success" ? triggerDigest(props.data.typeKey, props.rows) : null;

  if (!digest) return <ReceiptFooter {...props} />;

  return <ReceiptFooter {...props} labelSlot={<span className="wf-rf-digest" data-tone={digest.tone}>{digest.label}</span>} />;
}

/** A per-trigger terminal digest: the receipt label + the outcome tone that tints it. */
export interface TriggerDigest {
  label: ReactNode;
  tone: "success" | "warn" | "failure";
}

/**
 * The pure per-trigger receipt digest for a settled trigger — reads `rows[0].outputs` DEFENSIVELY (every field guarded
 * by `typeof`; a missing datum is skipped, never assumed) and returns the compact "what fired this" line plus a tone,
 * or null when the type is unknown / carries no output to summarize (the footer then shows the plain status bar).
 */
export function triggerDigest(typeKey: string, rows: WorkflowRunNodeSummary[]): TriggerDigest | null {
  const out = readOutputs(rows);
  if (!out) return null;

  switch (typeKey) {
    case "trigger.pr.opened":
    case "trigger.pr.updated":
    case "trigger.pr.merged":
      return prDigest(out);
    case "trigger.push":
      return pushDigest(out);
    case "trigger.schedule":
      return scheduleDigest(out);
    case "trigger.manual":
      return manualDigest(out);
    default:
      return null;
  }
}

/** trigger.pr.* → `#{number} {title-short}` (+ ` · {author}` when present); null when neither a number nor a title landed. */
function prDigest(out: Record<string, unknown>): TriggerDigest | null {
  const number = readNumber(out, "number");
  const title = readString(out, "title");
  const author = readString(out, "author");

  if (number == null && !title) return null;

  return {
    tone: "success",
    label: (
      <>
        {number != null && <span className="wf-rf-digest-mono">#{number}</span>}
        {title && <span className="wf-rf-digest-text">{shortenTitle(title)}</span>}
        {author && <span className="wf-rf-digest-text"> · {author}</span>}
      </>
    ),
  };
}

/** trigger.push → `{branch} · {commitCount} commits · {afterSha7}`, dropping any part that's absent; null when the whole push is empty. */
function pushDigest(out: Record<string, unknown>): TriggerDigest | null {
  const branch = readString(out, "branch") ?? readString(out, "ref");
  const commitCount = readNumber(out, "commitCount") ?? readArrayLength(out, "commits");
  const after = readString(out, "after") ?? readString(out, "afterSha") ?? readString(out, "sha");

  const bits: string[] = [];
  if (branch) bits.push(branch);
  if (commitCount != null) bits.push(`${commitCount} commits`);
  if (after) bits.push(after.slice(0, 7));

  if (bits.length === 0) return null;

  return { tone: "success", label: bits.join(" · ") };
}

/** trigger.schedule → the scheduledFor timestamp verbatim; null when absent. */
function scheduleDigest(out: Record<string, unknown>): TriggerDigest | null {
  const scheduledFor = readString(out, "scheduledFor");
  if (!scheduledFor) return null;

  return { tone: "success", label: scheduledFor };
}

/** trigger.manual → `由 {actor}` when the actor is known, else the neutral `手動`. */
function manualDigest(out: Record<string, unknown>): TriggerDigest {
  const actor = readString(out, "actor") ?? readString(out, "triggeredBy") ?? readString(out, "user");

  return { tone: "success", label: actor ? `由 ${actor}` : "手動" };
}

/** Trim a PR/issue title for the one-line receipt so a long title never widens the card. */
function shortenTitle(title: string): string {
  const trimmed = title.trim();
  return trimmed.length > 32 ? `${trimmed.slice(0, 32).trimEnd()}…` : trimmed;
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

/** The length of an array field, or undefined when the field isn't an array. */
function readArrayLength(out: Record<string, unknown>, key: string): number | undefined {
  const value = out[key];
  return Array.isArray(value) ? value.length : undefined;
}