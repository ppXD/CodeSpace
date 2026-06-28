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
export function AgentTerminal({ agent, onClose, rerun }: { agent: PhaseAgentRef; onClose?: () => void; rerun?: ReactNode }) {
  const [tab, setTab] = useState<"output" | "tools">("output");

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

  const name = agent.label || agent.nodeId || `agent ${agent.agentRunId.slice(0, 8)}`;
  const evts = events.data ?? [];

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
        <span className="agent-terminal-title" title={name}>{name}</span>
        {onClose && <button type="button" className="agent-terminal-close" onClick={onClose} aria-label="Collapse terminal"><Ic.Collapse size={13} /></button>}
      </div>

      {/* The model's allocation for this agent — its authored role + the subtask it was assigned. Absent for a
          homogeneous spawn / non-supervisor agent (both fields null). */}
      {(agent.role || agent.assignedSubtask) && (
        <div className="agent-terminal-alloc">
          {agent.role && <span className="agent-terminal-role">{agent.role}</span>}
          {agent.assignedSubtask && <span className="agent-terminal-subtask">{agent.assignedSubtask}</span>}
        </div>
      )}

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
        {tab === "output" ? <Scrollback events={evts} loading={events.isLoading && evts.length === 0} error={tileState(status) === "failed" ? run.data?.error ?? null : null} /> : <AgentToolCalls agentRunId={activeAgentRunId} />}
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
      {events.map((e) => (
        <li key={e.sequence} className="agent-terminal-row" data-kind={lineKind(e.kind)}>
          {isCommand(e.kind) && <span className="agent-terminal-prompt">❯ </span>}{e.text || humanize(e.kind)}
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
