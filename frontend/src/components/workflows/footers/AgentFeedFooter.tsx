/* eslint-disable react-refresh/only-export-components -- this module co-locates its render component with the small pure helpers (glyphs, formatters, digest/label builders) that only it and its sibling footers use; fast-refresh granularity is moot for these. */
import type { ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { isAgentRunActive, type AgentRunEventDto, type ToolCallView } from "@/api/agents";
import type { WorkflowRunNodeSummary } from "@/api/workflows";
import { useAgentRun, useAgentRunEvents, useToolCalls } from "@/hooks/use-agents";

import { parseTurnKey } from "../mapBranches";
import { formatTokens, formatUsd } from "../runActivity";
import { ReceiptFooter } from "./ReceiptFooter";
import type { NodeFooterProps } from "./index";

/**
 * The flagship live footer for the two Agent nodes (`agent.run`, `agent.supervisor`). Its source is the
 * bound agent run (`rows[0].agentRunId`), read through the agent-run hooks: {@link useAgentRun} for status,
 * {@link useAgentRunEvents} for the streamed event feed, {@link useToolCalls} for the governed tool-call
 * ledger. It reads in three moods:
 *
 *  - WORKING — while the run is active (or the node paints Running), a compact tail of the last 3 events
 *    (per-kind chip · kind · short text), a deduped changed-files count, and any token/cost the run detail
 *    carries. Header "代理工作中" with the shared spinner.
 *  - AWAITING APPROVAL — when the governed ledger has a tool call in `AwaitingApproval`, an AMBER banner
 *    "等待批准: {tool}" + an inline approve/deny row, so "the agent is working" vs "the agent needs YOU" are
 *    visually unmistakable. (The decision itself is not wired yet — see the TODO on {@link ApprovalBar}.)
 *  - TERMINAL — the shared {@link ReceiptFooter} bar + expand (which already embeds the agent timeline +
 *    tool-call audit), stamped with the run's summary · branch · +N 檔 · tokens · cost. For a supervisor, the
 *    stamp also carries the status triad tone so a `Stopped` run never reads as green success.
 *
 * Degrades cleanly: a node with no `agentRunId` (or one not reached yet) falls back to the plain receipt.
 * Every read of the open-ended event/output vocabulary is defensive — an unknown event kind becomes a
 * neutral dot, a missing datum is skipped, never assumed.
 */
export function AgentFeedFooter(props: NodeFooterProps) {
  // Resolve the agent run + its live sources. Hooks are called UNCONDITIONALLY (they accept undefined and
  // stay disabled), so the no-agent-run case takes no special hook path — it just yields empty data.
  const agentRunId = props.rows[0]?.agentRunId ?? null;
  const run = useAgentRun(agentRunId ?? undefined);
  const active = isAgentRunActive(run.data?.status);
  const events = useAgentRunEvents(agentRunId ?? undefined, active);
  const tools = useToolCalls(agentRunId ?? undefined, active);

  if (props.status === "Pending") return null;   // not reached yet → no footer (matches ReceiptFooter)

  const supervisor = props.data.typeKey === "agent.supervisor";

  // A governed tool call awaiting a human decision wins over the working feed — this is the "needs YOU" state.
  const pending = agentRunId ? firstPendingApproval(tools.data) : null;
  if (pending) return <ApprovalBar tool={pending} />;

  // Working: the agent is in flight, or the node is painted Running (poll not yet confirmed active).
  const live = !!agentRunId && (active || props.status === "Running");
  if (live) return <FeedBar events={events.data ?? []} metricsSource={run.data} supervisor={supervisor} rows={props.rows} />;

  // Terminal (or a run-less receipt): the shared bar + expand, stamped with the agent/supervisor digest.
  // A null stamp lets ReceiptFooter render its plain status label (the clean degrade).
  return <ReceiptFooter {...props} labelSlot={agentReceiptStamp(props.rows[0]?.outputs, supervisor)} />;
}

// ─── Live working feed ───────────────────────────────────────────────────────────

/** The last-3-events working feed: header + a ≤3-row tail (older rows fade), a changed-files count, live metrics. */
function FeedBar({ events, metricsSource, supervisor, rows }: { events: AgentRunEventDto[]; metricsSource: unknown; supervisor: boolean; rows: WorkflowRunNodeSummary[] }) {
  const tail = events.slice(-3);                 // newest at the bottom; DOM stays ≤3 rows
  const changed = countChangedFiles(events);
  const metrics = readAgentMetrics(metricsSource);   // token/cost only when the run detail carries them (absent today)
  const turn = supervisor ? currentTurn(rows) : null;

  const hasMeta = changed > 0 || metrics.tokens != null || metrics.costUsd != null;

  return (
    <div className="wf-rf-result wf-rf-feed nodrag nopan" data-status="running">
      <div className="wf-rf-feed-head">
        <span className="wf-rf-status-spin" aria-hidden="true" />
        <span className="wf-rf-feed-title">代理工作中</span>
        {turn != null && <span className="wf-rf-feed-turn">turn {turn}</span>}
        {hasMeta && (
          <span className="wf-rf-feed-meta">
            {changed > 0 && <span>{changed} 檔</span>}
            {metrics.tokens != null && <span>{formatTokens(metrics.tokens)}</span>}
            {metrics.costUsd != null && <span>{formatUsd(metrics.costUsd)}</span>}
          </span>
        )}
      </div>
      {tail.length > 0 && (
        <ol className="wf-rf-feed-list">
          {tail.map((e) => {
            const { key, Icon } = eventIcon(e.kind);
            return (
              <li key={e.sequence} className="wf-rf-feed-row" data-kind={e.kind}>
                <span className="wf-rf-feed-ic" data-icon={key} aria-hidden="true"><Icon size={12} /></span>
                <span className="wf-rf-feed-kind">{e.kind}</span>
                <span className="wf-rf-feed-text">{shortText(e.text)}</span>
              </li>
            );
          })}
        </ol>
      )}
    </div>
  );
}

// ─── Awaiting-approval state ──────────────────────────────────────────────────────

/**
 * The amber "the agent needs a human decision" state — a governed tool call is parked in `AwaitingApproval`.
 *
 * TODO(B3-followup): wire the approve/deny buttons to the governed tool-call decision endpoint. Today the
 * ledger row ({@link ToolCallView} from {@link useToolCalls}) is read-only and carries no decision id or the
 * call's arguments, and {@link "../AgentToolCalls".AgentToolCalls} exposes no decision mutation to reuse — so
 * these are affordances, not yet a live API call (the task's "render an affordance, don't duplicate an API
 * call" branch). The tool name is shown; the call args aren't in the DTO, so no `{short arg}` is available.
 */
function ApprovalBar({ tool }: { tool: ToolCallView }) {
  const stop = (e: React.MouseEvent) => e.stopPropagation();

  return (
    <div className="wf-rf-result wf-rf-feed nodrag nopan" data-approval role="group" aria-label={`等待批准 ${tool.toolKind}`}>
      <div className="wf-rf-feed-head">
        <span className="wf-rf-feed-ic" data-icon="shield" aria-hidden="true"><Ic.Shield size={12} /></span>
        <span className="wf-rf-feed-title">等待批准: {tool.toolKind}</span>
      </div>
      <div className="wf-rf-approve-row">
        <button type="button" className="wf-rf-approve" onClick={stop} title="批准 — 尚未接線 (B3-followup)">批准</button>
        <button type="button" className="wf-rf-deny" onClick={stop} title="拒絕 — 尚未接線 (B3-followup)">拒絕</button>
      </div>
    </div>
  );
}

// ─── Terminal receipt stamp ───────────────────────────────────────────────────────

/**
 * The terminal label that replaces the plain "Success" in the reused receipt bar: the run's summary (first line,
 * truncated), a mono branch chip, and a `+N 檔 · tokens · cost` metric strip — all read DEFENSIVELY off
 * `rows[0].outputs`. For a supervisor it also derives the status-triad tone (`Completed`=good, `Stopped`=warn,
 * `AcceptanceFailed`=failure) so a stopped run's stamp reads amber, never green. Returns null when there's
 * nothing agent-specific to stamp, so ReceiptFooter falls back to its default status label.
 */
function agentReceiptStamp(outputs: unknown, supervisor: boolean): ReactNode | null {
  const out = asObject(outputs);

  const summary = shortText(out ? readStr(out, "summary") : undefined, 48);
  const branch = out ? readStr(out, "branch") : undefined;
  const metrics = readAgentMetrics(out);
  const status = out ? readStr(out, "status") : undefined;
  const tone = supervisor ? supervisorTone(status) : undefined;

  const hasMetrics = metrics.changedFiles != null || metrics.tokens != null || metrics.costUsd != null;
  if (!summary && !branch && !hasMetrics && !tone) return null;

  const lead = summary || (supervisor && status ? supervisorLabel(status) : "");

  return (
    <span className="wf-rf-agent-stamp" data-tone={tone}>
      {lead && <span className="wf-rf-agent-lead">{lead}</span>}
      {branch && <span className="wf-rf-agent-branch"><Ic.Branch size={11} />{branch}</span>}
      {hasMetrics && (
        <span className="wf-rf-agent-metrics">
          {metrics.changedFiles != null && <span>+{metrics.changedFiles} 檔</span>}
          {metrics.tokens != null && <span>{formatTokens(metrics.tokens)}</span>}
          {metrics.costUsd != null && <span>{formatUsd(metrics.costUsd)}</span>}
        </span>
      )}
    </span>
  );
}

// ─── Event-kind icons (generic + a neutral dot fallback) ──────────────────────────

/** An icon component from the app's set — every `Ic.*` shares this renderer type. */
type IconRenderer = typeof Ic.Dot;

/**
 * Icon per event kind — a generic map over the OPEN event vocabulary; any kind not listed here (Queued /
 * Started / Completed, or a future/unknown kind) resolves to {@link DOT_ICON}, so the feed never crashes on
 * an unrecognised kind. The app's icon set has no flask/beaker or list-checks glyph, so TestOutput reuses the
 * Bug icon and PlanUpdate the Milestone icon — the closest semantic matches.
 */
const ICON_BY_EVENT_KIND: Record<string, { key: string; Icon: IconRenderer }> = {
  ToolCall:         { key: "wrench",      Icon: Ic.Wrench },
  CommandExecuted:  { key: "terminal",    Icon: Ic.Command },
  FileChanged:      { key: "file-diff",   Icon: Ic.File },
  TestOutput:       { key: "beaker",      Icon: Ic.Bug },
  AssistantMessage: { key: "chat",        Icon: Ic.Chat },
  Reasoning:        { key: "sparkle",     Icon: Ic.Sparkles },
  PlanUpdate:       { key: "list-checks", Icon: Ic.Milestone },
};

const DOT_ICON: { key: string; Icon: IconRenderer } = { key: "dot", Icon: Ic.Dot };

/** The icon spec for an event kind — its stable `key` (stamped as `data-icon` for styling + testing) and the glyph. */
export function eventIcon(kind: string): { key: string; Icon: IconRenderer } {
  return ICON_BY_EVENT_KIND[kind] ?? DOT_ICON;
}

// ─── Pure helpers ─────────────────────────────────────────────────────────────────

/** The first governed tool call parked awaiting a human decision, or null when none is pending. */
export function firstPendingApproval(tools: ToolCallView[] | undefined): ToolCallView | null {
  return tools?.find((t) => t.status === "AwaitingApproval") ?? null;
}

/** Distinct files touched, deduped by the FileChanged event's target (its text; the sequence as a defensive fallback). */
export function countChangedFiles(events: AgentRunEventDto[]): number {
  const files = new Set<string>();
  for (const e of events) {
    if (e.kind !== "FileChanged") continue;
    files.add((e.text ?? "").trim() || `#${e.sequence}`);
  }
  return files.size;
}

/** The current supervisor turn — the highest `#turn{N}` across the node's rows, or null when none carry one. */
export function currentTurn(rows: WorkflowRunNodeSummary[]): number | null {
  let max: number | null = null;
  for (const r of rows) {
    const t = parseTurnKey(r.iterationKey);
    if (t && (max === null || t.turn > max)) max = t.turn;
  }
  return max;
}

/** The status-triad tone for a supervisor's terminal outcome — a Stopped run must NOT read as green success. */
export function supervisorTone(status: string | undefined): "success" | "warn" | "failure" | undefined {
  if (status === "Stopped") return "warn";
  if (status === "AcceptanceFailed") return "failure";
  if (status === "Completed") return "success";
  return undefined;
}

/** A short human label for a supervisor terminal status (used when there's no summary to lead with). */
function supervisorLabel(status: string): string {
  if (status === "Stopped") return "已停止";
  if (status === "AcceptanceFailed") return "驗收未過";
  if (status === "Completed") return "已完成";
  return status;
}

/** The token/cost/changed-files metrics an agent run or its output blob carries — every read defensive, all optional. */
interface AgentMetrics { changedFiles?: number; tokens?: number; costUsd?: number; }

export function readAgentMetrics(source: unknown): AgentMetrics {
  const o = asObject(source);
  if (!o) return {};

  return {
    changedFiles: readArrayLen(o, "changedFiles") ?? readNum(o, "filesChanged"),
    tokens: readNum(o, "totalTokens") ?? sumTokens(o),
    costUsd: readNum(o, "costUsd") ?? readNum(o, "estimatedCostUsd"),
  };
}

/** input+output tokens when either is present, else undefined (so an all-absent blob contributes no token line). */
function sumTokens(o: Record<string, unknown>): number | undefined {
  const inp = readNum(o, "inputTokens");
  const out = readNum(o, "outputTokens");
  if (inp == null && out == null) return undefined;
  return (inp ?? 0) + (out ?? 0);
}

/** Truncate an event/summary string to ~max chars; defensive against null/undefined. */
function shortText(text: string | null | undefined, max = 40): string {
  const t = (text ?? "").trim();
  return t.length > max ? `${t.slice(0, max)}…` : t;
}

/** A value as a plain keyed object, or null when absent / non-object / an array (not a keyed receipt). */
function asObject(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

/** A finite numeric field, or undefined when missing / non-number. */
function readNum(o: Record<string, unknown>, key: string): number | undefined {
  const v = o[key];
  return typeof v === "number" && Number.isFinite(v) ? v : undefined;
}

/** A non-empty string field, or undefined when missing / non-string / empty. */
function readStr(o: Record<string, unknown>, key: string): string | undefined {
  const v = o[key];
  return typeof v === "string" && v.length > 0 ? v : undefined;
}

/** The length of an array field, or undefined when the field isn't an array. */
function readArrayLen(o: Record<string, unknown>, key: string): number | undefined {
  const v = o[key];
  return Array.isArray(v) ? v.length : undefined;
}