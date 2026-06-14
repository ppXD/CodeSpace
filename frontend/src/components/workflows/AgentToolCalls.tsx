import { Ic } from "@/_imported/ai-code-space/icons";
import { isAgentRunActive, type ToolCallLedgerStatus } from "@/api/agents";
import { useAgentRun, useToolCalls } from "@/hooks/use-agents";
import { useTeamMemberIdentityMap } from "@/hooks/use-team-members";

/**
 * The governed tool-call audit for one agent run, embedded under an `agent.code` step next to the live
 * {@link AgentRunTimeline}. Where the timeline streams what the agent *said/did*, this surfaces the durable
 * ledger of every side-effecting MCP tool call it made — what tool, the outcome, when, and who approved it —
 * so an operator can SEE the agent's governed actions and their approval trail. Read-only.
 *
 * Mirrors the timeline's pipeline: it reads the run's live status to know whether to poll (a new governed
 * call can land mid-run), and stops once terminal. Read-only tools never reach the ledger, so an absent list
 * is the common, expected case — shown as a plain empty state. Errors are already redacted at the source.
 */
export function AgentToolCalls({ agentRunId }: { agentRunId: string }) {
  const run = useAgentRun(agentRunId);
  const active = isAgentRunActive(run.data?.status);

  const toolCalls = useToolCalls(agentRunId, active);
  const rows = toolCalls.data ?? [];

  const identities = useTeamMemberIdentityMap();

  if (rows.length === 0) {
    // While loading we say nothing (the timeline already carries the run's live state); once we know the
    // list is genuinely empty, name it — the governed audit is empty because no side-effecting tool ran.
    if (toolCalls.isLoading) return null;
    return (
      <div className="tc-panel">
        <div className="tc-panel-head"><Ic.Command size={12} /> Tool calls</div>
        <div className="tc-empty">No governed tool calls for this run</div>
      </div>
    );
  }

  return (
    <div className="tc-panel">
      <div className="tc-panel-head"><Ic.Command size={12} /> Tool calls</div>
      <ol className="tc-list">
        {rows.map((c, i) => {
          const approverName = c.approvedByUserId ? identities.get(c.approvedByUserId)?.name ?? "Unknown" : null;

          return (
            <li key={i} className="tc-row" data-status={c.status}>
              <div className="tc-row-head">
                <span className="tc-tool">{c.toolKind}</span>
                <ToolCallStatusBadge status={c.status} />
                <span className="tc-when">{new Date(c.createdDate).toLocaleString()}</span>
              </div>
              {approverName && (
                <div className="tc-approver">
                  <Ic.Check size={11} /> approved by {approverName}
                  {c.approvedAt && <span className="tc-approver-at"> · {new Date(c.approvedAt).toLocaleString()}</span>}
                </div>
              )}
              {c.error && <pre className="wf-json wf-json-err tc-error">{c.error}</pre>}
            </li>
          );
        })}
      </ol>
    </div>
  );
}

/** Status pill for a governed tool call in the warm Claude theme — reuses the run-detail tone vocabulary. */
export function ToolCallStatusBadge({ status }: { status: ToolCallLedgerStatus }) {
  const tone =
    status === "Succeeded" ? "ok"
    : status === "Failed" || status === "Denied" ? "err"
    : status === "Expired" ? "muted"
    : status === "Pending" ? "queued"
    : "running"; // AwaitingApproval / Running → pending tone

  return <span className={`wf-status-pill wf-status-${tone}`}>{statusLabel(status)}</span>;
}

/** Human label for a tool-call status (camelCase enum → spaced words for the two-word states). */
function statusLabel(status: ToolCallLedgerStatus): string {
  return status === "AwaitingApproval" ? "Awaiting approval" : status;
}
