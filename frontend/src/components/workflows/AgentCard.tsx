import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PhaseAgentRef } from "@/api/workflows";
import { useAgentRun, useAgentRunEvents } from "@/hooks/use-agents";

import { AgentRunTimeline } from "./AgentRunTimeline";
import { AgentToolCalls } from "./AgentToolCalls";
import { RunStatusBadge } from "./RunStatusBadge";
import { isAgentBusy } from "./runPhases";

/**
 * One agent in the run's Live-work band — the restrained card the command-center design calls for: name + live
 * status, the current-activity line, and the harness, then EXPAND for the full event stream + governed tool
 * audit (reusing AgentRunTimeline + AgentToolCalls verbatim). The collapsed card stays glanceable so an N-agent
 * run reads at a glance. `status` is the agent run's LIVE status (falling back to the phase ref's status before
 * the agent row loads); "current activity" is its latest streamed event line. Per-agent actions (ask-to-adjust /
 * retry / stop) and the model/repo/files/tokens fields arrive with the later backend slices.
 */
export function AgentCard({ agent }: { agent: PhaseAgentRef }) {
  const [open, setOpen] = useState(false);
  const run = useAgentRun(agent.agentRunId);
  // Display the live agent-run status once loaded, falling back to the phase ref's status; both are open strings,
  // so isAgentBusy (string → busy?) classifies "in flight" for the accent + the event-poll gate.
  const status = run.data?.status ?? agent.status;
  const active = isAgentBusy(status);

  // The latest event is the "doing what" line; only the expand needs the full list, but the head line is cheap
  // (shared ['agent-run-events', id] cache with the expanded timeline, so opening the card costs no extra fetch).
  const events = useAgentRunEvents(agent.agentRunId, active);
  const latest = events.data && events.data.length > 0 ? events.data[events.data.length - 1] : undefined;

  const name = agent.label || agent.nodeId || `agent ${agent.agentRunId.slice(0, 8)}`;
  const activity = latest?.text ?? (active ? "Starting…" : "No activity recorded.");

  return (
    <div className="run-agent-card" data-active={active || undefined} data-open={open || undefined}>
      <button type="button" className="run-agent-card-head" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        <span className="run-agent-card-caret" aria-hidden="true"><Ic.ChevronRight size={11} /></span>
        <span className="run-agent-card-name">{name}</span>
        <RunStatusBadge status={status} />
      </button>

      <div className="run-agent-card-activity">{activity}</div>
      {run.data?.harness && <div className="run-agent-card-meta">{run.data.harness}</div>}

      {open && (
        <div className="run-agent-card-body">
          <AgentRunTimeline agentRunId={agent.agentRunId} />
          <AgentToolCalls agentRunId={agent.agentRunId} />
        </div>
      )}
    </div>
  );
}
