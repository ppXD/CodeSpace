import { useContext, useState, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentRunEventDto } from "@/api/agents";
import type { CellAttempt, NodeStatus, PhaseAgentRef } from "@/api/workflows";
import { useAgentRun, useAgentRunEvents } from "@/hooks/use-agents";
import { useCellAttempts } from "@/hooks/use-workflows";

import { AgentToolCalls } from "./AgentToolCalls";
import { RunActionsContext } from "./runActionsContext";
import { isAgentBusy } from "./runPhases";
import { formatDuration, formatTokens, formatUsd, tileState } from "./runActivity";

/**
 * The expanded agent terminal — opening an agent (a fleet dot, a table row, a tile) pops this full mac-terminal WINDOW
 * under the wave (ONE per wave). The title bar leads with the agent NAME; an identity strip carries harness · model ·
 * tool count · run time (the Slice-A rollup the ref already holds), so everything about the agent lives in one place.
 * The Output tab is the agent's whole event stream re-skinned as terminal scrollback (commands prompted, errors
 * toned); the Tool calls tab drills into the governed audit; the footer carries the live status + token + file rollup.
 * STILL read-only — there is no input; the window only reveals scrollback / tools, never a real shell. The full raw
 * ledger lives in the Trace tab.
 */
export function AgentTerminal({ agent, onClose, rerun, onOpenFile }: { agent: PhaseAgentRef; onClose?: () => void; rerun?: ReactNode; onOpenFile?: (path: string) => void }) {
  const [tab, setTab] = useState<"output" | "tools" | "files">("output");

  // Per-cell rerun history: every attempt that ran THIS (node, branch) cell. Picking an earlier one shows that
  // attempt's own record (the agent that ran then), so you can look back at e.g. the run that failed before a rerun.
  // The ref's agentRunId is the merged latest; null selection = that.
  const actions = useContext(RunActionsContext);
  const cellAttempts = useCellAttempts(actions?.runId ?? null, agent.nodeId, agent.iterationKey);
  const attemptRuns = (cellAttempts.data?.attempts ?? []).filter((a): a is CellAttempt & { agentRunId: string } => !!a.agentRunId);
  const [selectedAgentRunId, setSelectedAgentRunId] = useState<string | null>(null);
  const activeAgentRunId = selectedAgentRunId ?? agent.agentRunId;
  const viewingLatest = activeAgentRunId === agent.agentRunId;   // the ref's metrics (tokens/cost/files) only describe the latest

  const run = useAgentRun(activeAgentRunId);
  const status = run.data?.status ?? (viewingLatest ? agent.status : "Running");
  const active = isAgentBusy(status);
  const events = useAgentRunEvents(activeAgentRunId, active);

  // Prefer the MEANINGFUL name (a supervisor agent's role / assigned subtask) over the structural node id, so the
  // title reads the same in every surface (run detail + the session room) and the alloc strip suppresses its duplicate.
  const name = agent.label || agent.role || agent.assignedSubtask || agent.nodeId || `agent ${agent.agentRunId.slice(0, 8)}`;
  const evts = events.data ?? [];

  // This agent's own changed files (per-agent attribution) — a Files tab, shown only when it produced any. The
  // assigned-subtask strip is suppressed when it merely repeats the title (the terminal bar already leads with it).
  const files = viewingLatest ? agent.changedFiles ?? [] : [];
  const showSubtask = !!agent.assignedSubtask && agent.assignedSubtask !== name;
  // The bar leads (right of the lights) with the READABLE title — the assigned subtask when it adds over the name (the
  // name / id is already in the drawer header, so the bar needn't repeat it), else the name. The authored role rides as a
  // muted secondary. One bar line — no separate header row below it.
  const barName = showSubtask ? agent.assignedSubtask! : name;
  const barRole = agent.role && agent.role !== barName ? agent.role : null;

  // The metrics shown describe the ACTIVE attempt: the ref carries the merged-latest figures, while an earlier
  // attempt carries its OWN (from its agent run) — so switching to a failed/older attempt shows that attempt's
  // spend + timing + model, not the latest's (and a failed attempt's tokens, no longer hidden behind viewingLatest).
  const sel = attemptRuns.find((a) => a.agentRunId === activeAgentRunId);
  const m = viewingLatest
    ? { tokens: (agent.inputTokens ?? 0) + (agent.outputTokens ?? 0), cost: agent.costUsd ?? 0, files: agent.filesChanged ?? 0, model: agent.model, toolCount: agent.toolCount, durationMs: agent.durationMs }
    : { tokens: (sel?.inputTokens ?? 0) + (sel?.outputTokens ?? 0), cost: sel?.costUsd ?? 0, files: sel?.filesChanged ?? 0, model: sel?.model ?? null, toolCount: sel?.toolCount ?? null, durationMs: sel?.durationMs ?? null };

  // The agent's identity strip — harness (the live run row) + model + tools + time (the active attempt's rollup). Each part drops when absent.
  const identity = [
    run.data?.harness,
    m.model,
    m.toolCount != null && `${m.toolCount} ${m.toolCount === 1 ? "tool" : "tools"}`,
    m.durationMs != null && formatDuration(m.durationMs),
  ].filter(Boolean) as string[];

  return (
    <div className="agent-terminal" data-state={tileState(status)}>
      <div className="agent-terminal-bar">
        <span className="agent-terminal-lights" aria-hidden="true"><i></i><i></i><i></i></span>
        <span className="agent-terminal-title" title={barName}>{barName}</span>
        {barRole && <span className="agent-terminal-barsub" title={barRole}>{barRole}</span>}
        {onClose && <button type="button" className="agent-terminal-close" onClick={onClose} aria-label="Collapse terminal"><Ic.Collapse size={13} /></button>}
      </div>

      {identity.length > 0 && (
        <div className="agent-terminal-meta">
          {identity.map((part, i) => <span key={i}>{part}</span>)}
        </div>
      )}

      {/* The exact GOAL / instruction this agent was given (its prompt) — collapsible since it can be long. Lets you
          see WHAT the supervisor told this agent to do, not just its output. Absent when the task blob is unavailable. */}
      {run.data?.goal && (
        <details className="agent-terminal-goal">
          <summary>Instruction</summary>
          <div className="agent-terminal-goal-body">{run.data.goal}</div>
        </details>
      )}

      {attemptRuns.length > 1 && (
        <div className="agent-terminal-attempts" role="tablist" aria-label="This node's rerun history">
          <span className="agent-terminal-attempts-label">Ran</span>
          {attemptRuns.map((a) => (
            <button
              key={a.runId}
              type="button"
              role="tab"
              aria-selected={a.agentRunId === activeAgentRunId}
              className="agent-terminal-attempt"
              data-selected={a.agentRunId === activeAgentRunId || undefined}
              title={`Attempt ${a.attemptNumber} · ${a.status}`}
              onClick={() => setSelectedAgentRunId(a.agentRunId)}
            >
              <AttemptGlyph status={a.status} /> Attempt {a.attemptNumber}{a.isLatest && <span className="agent-terminal-attempt-latest">latest</span>}
            </button>
          ))}
        </div>
      )}

      <div className="agent-terminal-body">
        {tab === "output"
          ? <Scrollback events={evts} loading={events.isLoading && evts.length === 0} error={tileState(status) === "failed" ? run.data?.error ?? null : null} />
          : tab === "tools" ? <AgentToolCalls agentRunId={activeAgentRunId} hideHeader />
          : <AgentFiles files={files} onOpenFile={onOpenFile} />}
      </div>

      <div className="agent-terminal-footer">
        <span className="agent-terminal-stat" data-state={tileState(status)}><span className="agent-terminal-statdot" aria-hidden="true"></span>{humanize(status)}</span>
        {rerun}
        {m.tokens > 0 && <span className="agent-terminal-fact">{formatTokens(m.tokens)} tokens</span>}
        {m.cost > 0 && <span className="agent-terminal-fact">{formatUsd(m.cost)}</span>}
        {m.files > 0 && <span className="agent-terminal-fact">{m.files} {m.files === 1 ? "file" : "files"}</span>}
        <div className="agent-terminal-tabs">
          <button type="button" data-active={tab === "output" || undefined} onClick={() => setTab("output")}>Output</button>
          <button type="button" data-active={tab === "tools" || undefined} onClick={() => setTab("tools")}>Tool calls</button>
          {files.length > 0 && <button type="button" data-active={tab === "files" || undefined} onClick={() => setTab("files")}>Files</button>}
        </div>
      </div>
    </div>
  );
}

function Scrollback({ events, loading, error }: { events: AgentRunEventDto[]; loading: boolean; error: string | null }) {
  if (events.length === 0) {
    // A run that failed BEFORE emitting any event (a dispatch / harness error) carries its reason on the run's `error`,
    // not in the empty stream — surface it as an error line so a failed terminal always says WHY, never "No output yet.".
    if (error) return <ol className="agent-terminal-scroll"><li className="agent-terminal-row" data-kind="error">{error}</li></ol>;
    return <div className="agent-terminal-empty">{loading ? "Connecting to the sandbox…" : "No output yet."}</div>;
  }

  return (
    <ol className="agent-terminal-scroll">
      {events.map((e) => {
        const line = codexLine(e);
        if (line === null) return null; // a suppressed codex lifecycle line (a duplicate item.started / a bare turn.started)
        return (
          <li key={e.sequence} className="agent-terminal-row" data-kind={lineKind(e.kind)}>
            {isCommand(e.kind) && <span className="agent-terminal-prompt">❯ </span>}{line}
          </li>
        );
      })}
    </ol>
  );
}

/** Codex 0.142.x nests each event's readable payload under `item`. New runs are normalized at capture, but runs recorded
 *  before the harness learned that schema stored the bare envelope name ("item.completed") as the text. When the text is a
 *  bare name and the raw data is still on the wire, recover the readable line from it — the same fields the backend
 *  normalizer extracts — so an OLD codex run reads like a NEW one. Returns null for a pure-lifecycle line that should drop,
 *  and passes every non-codex / already-readable line straight through. */
const CODEX_BARE = /^(thread|turn|item)\.(started|completed|failed)$/;
function codexLine(e: AgentRunEventDto): string | null {
  if (!CODEX_BARE.test(e.text)) return e.text || humanize(e.kind);
  if (e.text === "item.started" || e.text === "turn.started") return null;
  if (!e.data) return e.text; // offloaded payload — can't recover, keep the bare name rather than invent one

  try {
    const root = JSON.parse(e.data);
    if (root?.type === "thread.started") return "Session started";
    if (root?.type === "turn.completed") return "Turn complete";
    if (root?.type === "turn.failed") return typeof root?.error?.message === "string" ? root.error.message : e.text;

    const item = root?.item;
    if (item && typeof item === "object") {
      if (item.type === "todo_list" && Array.isArray(item.items)) {
        const lines = item.items.filter((t: { text?: string }) => t?.text).map((t: { text: string; completed?: boolean }) => `${t.completed ? "[x]" : "[ ]"} ${t.text}`);
        return lines.length > 0 ? lines.join("\n") : e.text;
      }
      for (const k of ["text", "message", "command", "aggregated_output"]) {
        if (typeof item[k] === "string" && item[k]) return item[k];
      }
    }
  } catch { /* malformed data — fall through to the bare name */ }

  return e.text;
}

/** The Files tab — the files THIS agent changed. Each is openable (a preview) when the host provides `onOpenFile`, else a plain listing. */
function AgentFiles({ files, onOpenFile }: { files: string[]; onOpenFile?: (path: string) => void }) {
  if (files.length === 0) return <div className="agent-terminal-empty">No files changed.</div>;

  return (
    <ol className="agent-terminal-files">
      {files.map((p) => (
        <li key={p}>
          {onOpenFile
            ? <button type="button" className="agent-terminal-file" onClick={() => onOpenFile(p)}><Ic.File size={12} aria-hidden="true" /><span>{p}</span></button>
            : <span className="agent-terminal-file" data-static="true"><Ic.File size={12} aria-hidden="true" /><span>{p}</span></span>}
        </li>
      ))}
    </ol>
  );
}

/** A cell-attempt's outcome glyph — tick when it succeeded, cross when it failed, clock while live/pending. */
function AttemptGlyph({ status }: { status: NodeStatus }) {
  if (status === "Success") return <span className="agent-terminal-attempt-glyph" data-tone="success"><Ic.Check size={11} aria-hidden="true" /></span>;
  if (status === "Failure") return <span className="agent-terminal-attempt-glyph" data-tone="failed"><Ic.X size={11} aria-hidden="true" /></span>;
  return <span className="agent-terminal-attempt-glyph" data-tone="live"><Ic.Clock size={11} aria-hidden="true" /></span>;
}

/** "TimedOut" → "timed out", "FinalSummary" → "final summary" — split camelCase + lowercase for a micro-label. */
function humanize(s: string): string {
  return s.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
}

/** A command-shaped event gets the prompt; the rest are output lines. */
function isCommand(kind: string): boolean {
  return kind === "CommandExecuted" || kind === "ToolCall";
}

/** The line's tone — errors danger, warnings amber, commands accented, everything else plain output. */
function lineKind(kind: string): "error" | "warn" | "command" | "out" {
  if (kind === "Error") return "error";
  if (kind === "Warning") return "warn";
  if (isCommand(kind)) return "command";
  return "out";
}
