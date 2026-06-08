import { Ic } from "@/_imported/ai-code-space/icons";
import { useWorkflowRuns } from "@/hooks/use-workflows";

import { RunStatusBadge } from "./RunDetailView";

/**
 * Agent "Activity" tab — the list of recent runs for this agent (same data + row shape as the
 * RunHistoryDialog / run-list, surfaced as a tab). Owns its own fetch so the tab is self-contained;
 * clicking a row opens that run via onOpenRun. "a run" stays the noun for one execution; the
 * section is "Activity" (agent-first naming).
 */
export function AgentActivityPanel({ workflowId, onOpenRun }: {
  workflowId: string;
  onOpenRun: (runId: string) => void;
}) {
  const runs = useWorkflowRuns(workflowId);
  const rows = runs.data ?? [];

  if (runs.isLoading) {
    return <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>;
  }

  if (rows.length === 0) {
    return (
      <div className="ct-empty">
        <div className="ct-empty-h">No activity yet</div>
        <div className="ct-empty-p">This agent hasn't run yet — click <strong>Run now</strong> or wait for a trigger to fire.</div>
      </div>
    );
  }

  return (
    <ul className="agent-activity">
      {rows.map((r) => (
        <li key={r.id} className="agent-activity-row" onClick={() => onOpenRun(r.id)} title={`Run ${r.id.slice(0, 8)}`}>
          <RunStatusBadge status={r.status} />
          <span className="agent-activity-id">{r.id.slice(0, 8)}</span>
          <span className="agent-activity-src">{r.sourceType}</span>
          <span className="agent-activity-time">{r.startedAt ? new Date(r.startedAt).toLocaleString() : "—"}</span>
          <span className="agent-activity-ver">v{r.workflowVersion}</span>
          <Ic.ChevronRight size={12} />
        </li>
      ))}
    </ul>
  );
}
