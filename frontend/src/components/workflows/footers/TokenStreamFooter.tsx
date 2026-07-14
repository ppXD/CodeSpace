/* eslint-disable react-refresh/only-export-components -- this module co-locates its render component with the small pure helpers (glyphs, formatters, digest/label builders) that only it and its sibling footers use; fast-refresh granularity is moot for these. */
import type { ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunNodeSummary } from "@/api/workflows";
import { formatElapsed, useNowTick } from "@/hooks/use-now-tick";
import { useNodeLiveContext } from "@/hooks/use-run-live";

import { ReceiptFooter } from "./ReceiptFooter";
import type { NodeFooterProps } from "./index";

/**
 * The footer for the AI / LLM family — every typeKey the registry resolves to the `tokenStream` kind (`llm.complete`,
 * `plan.author`, and any future AI/Planning node). It reads the same coze-style `.wf-rf-result` language as
 * {@link ReceiptFooter} but adds the one thing a plain receipt can't show: the LIVE generation, off the
 * {@link useNodeLiveContext} interaction-delta signal (A4). WHILE STREAMING it paints a `生成中` bar with a blinking
 * caret + a right-aligned ≈token estimate (chars÷4) + three shimmer lines (a TEXTURE that conveys "text flowing" — never
 * fabricated text; counts only, so memory stays flat). WHILE BUFFERED (a non-streamed call — no live deltas) it shows a
 * gentle sparkle + elapsed instead, with NO fake shimmer. ONCE TERMINAL it stamps a generic token digest
 * ({@link digestTokens}) — "1,200→350 tok · $0.02 · json ✓" — onto ReceiptFooter's exact bar + expand panel. Degrades
 * cleanly: no live store (editor / no run) → renders from `rows` alone (buffered/terminal, never throwing).
 */
export function TokenStreamFooter(props: NodeFooterProps) {
  const live = useNodeLiveContext(props.data.nodeId);
  const now = useNowTick();

  if (props.status === "Running") {
    return live?.stream?.streaming
      ? <StreamingBar chars={live.stream.chars} title={props.title} />
      : <BufferedBar rows={props.rows} now={now} title={props.title} />;
  }

  const digest = digestTokens(props.rows);

  if (!digest) return <ReceiptFooter {...props} />;

  return <ReceiptFooter {...props} labelSlot={<span className="wf-rf-digest" data-tone={digest.tone}>{digest.label}</span>} />;
}

/** The live streaming bar: `生成中` + a blinking caret, a right-aligned ≈token estimate, and 3 shimmer lines (texture, not text). */
function StreamingBar({ chars, title }: { chars: number; title?: string }) {
  const tokens = Math.round(chars / 4);

  return (
    <div className="wf-rf-result wf-rf-tok nodrag nopan" data-status="running">
      <div className="wf-rf-result-bar" title={title}>
        <span className="wf-rf-result-glyph" aria-hidden="true"><Ic.Sparkles size={12} /></span>
        <span className="wf-rf-result-label">生成中</span>
        <span className="wf-rf-tok-caret" aria-hidden="true" />
        <span className="wf-rf-tok-count">≈{tokens.toLocaleString()} tok</span>
      </div>
      <div className="wf-rf-tok-lines" aria-hidden="true">
        <span className="wf-rf-tok-line" />
        <span className="wf-rf-tok-line" />
        <span className="wf-rf-tok-line" />
      </div>
    </div>
  );
}

/** The buffered running bar (a non-streamed call): a gentle sparkle + elapsed when the row carries a start; otherwise just the spinner. No shimmer — nothing is streaming. */
function BufferedBar({ rows, now, title }: { rows: WorkflowRunNodeSummary[]; now: number; title?: string }) {
  const startedAt = rows[0]?.startedAt;
  const start = startedAt ? Date.parse(startedAt) : NaN;
  const hasStart = Number.isFinite(start);

  return (
    <div className="wf-rf-result wf-rf-tok nodrag nopan" data-status="running">
      <div className="wf-rf-result-bar" title={title}>
        <span className="wf-rf-result-glyph" aria-hidden="true">
          {hasStart ? <span className="wf-rf-tok-spark"><Ic.Sparkles size={12} /></span> : <span className="wf-rf-status-spin" />}
        </span>
        <span className="wf-rf-result-label">生成中</span>
        {hasStart && <span className="wf-rf-result-dur">{formatElapsed(now - start)}</span>}
      </div>
    </div>
  );
}

/** A generic terminal token digest: the receipt label + the outcome tone that tints it (warn when the output was truncated). */
export interface TokenDigest {
  label: ReactNode;
  tone: "success" | "warn";
}

/**
 * The pure, generic terminal digest for an AI node — reads `rows[0].outputs` DEFENSIVELY (every field guarded by
 * `typeof`; a missing datum is skipped, never assumed) and returns the compact "what the call produced" line plus an
 * outcome tone, or null when no token counts landed (the footer then falls back to the plain status·duration bar, so a
 * `plan.author` or any AI node without token outputs still reads cleanly). Shape: `{in}→{out} tok`, then `· ${cost}` when
 * `costUsd` is present (formatted to cents), then a `json ✓` marker when an `outputs.json` object exists. A
 * `finishReason === "length"` (the model hit its output cap) flips the tone to `warn` and appends `· 已截斷`.
 */
export function digestTokens(rows: WorkflowRunNodeSummary[]): TokenDigest | null {
  const out = readOutputs(rows);
  if (!out) return null;

  const inTok = readNumber(out, "inputTokens");
  const outTok = readNumber(out, "outputTokens");
  if (inTok === undefined && outTok === undefined) return null;

  const cost = readNumber(out, "costUsd");
  const hasJson = isObject(out["json"]);
  const truncated = readString(out, "finishReason") === "length";

  return {
    tone: truncated ? "warn" : "success",
    label: (
      <>
        <span className="wf-rf-digest-mono">{(inTok ?? 0).toLocaleString()}</span>
        →
        <span className="wf-rf-digest-mono">{(outTok ?? 0).toLocaleString()}</span>
        {" tok"}
        {cost !== undefined && <> · <span className="wf-rf-digest-mono">{formatCostUsd(cost)}</span></>}
        {hasJson && <> · <span className="wf-rf-tok-json">json ✓</span></>}
        {truncated && " · 已截斷"}
      </>
    ),
  };
}

/** A per-call USD cost trimmed to cents: `$0.02`; a positive sub-cent cost reads `<$0.01` so a real charge never rounds to a bare `$0.00`. */
function formatCostUsd(n: number): string {
  if (n > 0 && n < 0.01) return "<$0.01";
  return `$${n.toFixed(2)}`;
}

/** rows[0].outputs as a plain object, or null when absent / non-object (an array output isn't a keyed receipt). */
function readOutputs(rows: WorkflowRunNodeSummary[]): Record<string, unknown> | null {
  const outputs = rows[0]?.outputs;
  return isObject(outputs) ? (outputs as Record<string, unknown>) : null;
}

/** True for a non-null, non-array object. */
function isObject(value: unknown): boolean {
  return value !== null && typeof value === "object" && !Array.isArray(value);
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