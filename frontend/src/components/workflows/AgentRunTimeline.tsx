import { isAgentRunActive } from "@/api/agents";
import { useAgentRun, useAgentRunEvents } from "@/hooks/use-agents";

/**
 * Live, chat-style monitor for one agent run, embedded under an `agent.code` step in the run detail. While
 * the run is in flight it polls its status + event log every ~2s, so you watch each step (assistant
 * messages, tool/command calls, file edits, the final summary, errors) stream in real time — and it shows
 * "live · active Ns ago" off the heartbeat so the run is never an opaque "Suspended". Polling stops the
 * moment the run is terminal. Secrets are already redacted at the source.
 */
export function AgentRunTimeline({ agentRunId }: { agentRunId: string }) {
  const run = useAgentRun(agentRunId);
  const status = run.data?.status;
  const active = isAgentRunActive(status);

  const events = useAgentRunEvents(agentRunId, active);
  const rows = events.data ?? [];

  return (
    <div className="ar-timeline">
      <div className="ar-timeline-head">
        <span className={`ar-status ar-status-${(status ?? "loading").toLowerCase()}`}>{status ?? "…"}</span>
        {active && run.data?.heartbeatAt && (
          <span className="ar-live"><span className="ar-live-dot" /> live · active {relTime(run.data.heartbeatAt)}</span>
        )}
        {run.data?.harness && <span className="ar-harness">{run.data.harness}</span>}
      </div>

      {run.data?.error && <pre className="wf-json wf-json-err">{run.data.error}</pre>}

      {rows.length === 0 ? (
        <div className="ar-empty">{active ? "Waiting for the agent's first output…" : "No activity recorded."}</div>
      ) : (
        <ol className="ar-events">
          {rows.map((e) => (
            <li key={e.sequence} className="ar-event" data-kind={e.kind}>
              <span className="ar-event-kind">{kindLabel(e.kind)}</span>
              <span className="ar-event-text">{e.text}</span>
              <span className="ar-event-time">{new Date(e.occurredAt).toLocaleTimeString()}</span>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

/** Short, human label for an event kind ("CommandExecuted" → "ran", "FileChanged" → "edited", …). */
function kindLabel(kind: string): string {
  switch (kind) {
    case "AssistantMessage": return "says";
    case "ToolCall": return "reads";
    case "CommandExecuted": return "ran";
    case "FileChanged": return "edited";
    case "Completed": return "done";
    case "FinalSummary": return "summary";
    case "Error": return "error";
    case "Warning": return "warn";
    default: return kind.toLowerCase();
  }
}

/** "just now" / "Ns ago" / "Nm ago" off an ISO timestamp — the heartbeat freshness signal. */
function relTime(iso: string): string {
  const secs = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (secs < 5) return "just now";
  if (secs < 60) return `${secs}s ago`;
  if (secs < 3600) return `${Math.floor(secs / 60)}m ago`;
  return `${Math.floor(secs / 3600)}h ago`;
}
