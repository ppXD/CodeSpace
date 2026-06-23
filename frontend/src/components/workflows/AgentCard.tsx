import { useEffect, useRef, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PhaseAgentRef } from "@/api/workflows";
import { useAgentRun, useAgentRunEvents } from "@/hooks/use-agents";

import { AgentRunTimeline } from "./AgentRunTimeline";
import { AgentToolCalls } from "./AgentToolCalls";
import { RunStatusBadge } from "./RunStatusBadge";
import { formatDuration } from "./cockpit";
import { isAgentBusy } from "./runPhases";

/** Join the truthy parts of an agent card's meta line with " · " (harness · N files · M tools · duration). */
function metaLine(parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(" · ");
}

/**
 * One agent in the run's Live-work band — the restrained card the command-center design calls for: name + live
 * status, the current-activity line, and the harness, then EXPAND for the full event stream + governed tool
 * audit (reusing AgentRunTimeline + AgentToolCalls verbatim). The collapsed card stays glanceable so an N-agent
 * run reads at a glance. `status` is the agent run's LIVE status (falling back to the phase ref's status before
 * the agent row loads); "current activity" is its latest streamed event line. Per-agent actions (ask-to-adjust /
 * retry / stop) and the model/repo/files/tokens fields arrive with the later backend slices.
 */
export function AgentCard({ agent, selected }: { agent: PhaseAgentRef; selected?: boolean }) {
  const [open, setOpen] = useState(false);
  const cardRef = useRef<HTMLDivElement>(null);

  // When the outline selects this agent, bring its card into view (within the center panel's own scroll).
  useEffect(() => {
    if (selected) cardRef.current?.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }, [selected]);

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
  // "…" while the first event fetch is still in flight, so a terminal agent doesn't flash "No activity" then swap.
  const activity = latest?.text ?? (active ? "Starting…" : events.data === undefined ? "…" : "No activity recorded.");

  // Client-side rollups from what's already on the wire: file edits + tool uses counted off the event stream,
  // elapsed/duration off the run row. The model / token / cost fields need a backend rollup (a later slice).
  const evts = events.data ?? [];
  const files = evts.filter((e) => e.kind === "FileChanged").length;
  const tools = evts.filter((e) => e.kind === "ToolCall").length;
  // Duration is shown once the agent settles (a ticking elapsed for a live run would need an impure clock; the
  // status badge + the expand's "active Ns ago" heartbeat already convey liveness).
  const duration = run.data?.completedAt && run.data.createdDate ? formatDuration(run.data.createdDate, run.data.completedAt) : "";

  const meta = metaLine([
    run.data?.harness,
    files > 0 && `${files} ${files === 1 ? "file" : "files"}`,
    tools > 0 && `${tools} ${tools === 1 ? "tool" : "tools"}`,
    duration,
  ]);

  return (
    <div className="run-agent-card" ref={cardRef} data-active={active || undefined} data-open={open || undefined} data-selected={selected || undefined}>
      <button type="button" className="run-agent-card-head" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        <span className="run-agent-card-caret" aria-hidden="true"><Ic.ChevronRight size={11} /></span>
        <span className="run-agent-card-name">{name}</span>
        <RunStatusBadge status={status} />
      </button>

      <div className="run-agent-card-activity">{activity}</div>
      {meta && <div className="run-agent-card-meta">{meta}</div>}

      {open && (
        <div className="run-agent-card-body">
          <AgentRunTimeline agentRunId={agent.agentRunId} />
          <AgentToolCalls agentRunId={agent.agentRunId} />
        </div>
      )}
    </div>
  );
}
