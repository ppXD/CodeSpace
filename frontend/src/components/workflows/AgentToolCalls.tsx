import { Ic } from "@/_imported/ai-code-space/icons";
import { isAgentRunActive, type ToolCallLedgerStatus } from "@/api/agents";
import { useAgentRun, useAgentRunEvents, useToolCalls } from "@/hooks/use-agents";
import { useTeamMemberIdentityMap } from "@/hooks/use-team-members";

/**
 * The tool-call view for one agent run. It prefers the GOVERNED ledger — every side-effecting MCP tool call with its
 * outcome + approval trail — when a run routed calls through governance. When that ledger is empty (the common case: a
 * Codex / Claude-Code run uses its OWN harness-native tools, which never touch the MCP fabric), it falls back to the
 * agent's ACTUAL tool calls off the append-only event stream, so the tab shows what the agent really did (Read / Edit /
 * WebSearch …) rather than a misleading "none". Read-only. Errors are already redacted at the source.
 */
export function AgentToolCalls({ agentRunId }: { agentRunId: string }) {
  const run = useAgentRun(agentRunId);
  const active = isAgentRunActive(run.data?.status);

  const toolCalls = useToolCalls(agentRunId, active);
  const governed = toolCalls.data ?? [];

  const events = useAgentRunEvents(agentRunId, active);
  const native = (events.data ?? []).filter((e) => e.kind === "ToolCall");

  const identities = useTeamMemberIdentityMap();

  if (governed.length === 0 && native.length === 0) {
    // While loading we say nothing (the timeline already carries the run's live state); once we know both are
    // genuinely empty, name it — the agent made no tool calls at all.
    if (toolCalls.isLoading || events.isLoading) return null;
    return (
      <div className="tc-panel">
        <div className="tc-panel-head"><Ic.Command size={12} /> Tool calls</div>
        <div className="tc-empty">No tool calls for this run</div>
      </div>
    );
  }

  return (
    <div className="tc-panel">
      <div className="tc-panel-head"><Ic.Command size={12} /> Tool calls</div>
      {governed.length > 0 ? (
        <ol className="tc-list">
          {governed.map((c, i) => {
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
      ) : (
        <ol className="tc-list">
          {native.map((e) => {
            const args = argPreview(e.data);
            return (
              <li key={e.sequence} className="tc-row" data-status="Succeeded">
                <div className="tc-row-head">
                  <span className="tc-tool">{e.text || "tool"}</span>
                  <span className="tc-when">{new Date(e.occurredAt).toLocaleTimeString()}</span>
                </div>
                {args && <div className="tc-args">{args}</div>}
              </li>
            );
          })}
        </ol>
      )}
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

/** A compact one-line argument preview from a ToolCall event's payload — the args minus the bookkeeping id/name. Null when there's nothing useful to show. */
function argPreview(data: string | null): string | null {
  if (!data) return null;
  try {
    const parsed = JSON.parse(data) as Record<string, unknown>;
    const { id: _id, name: _name, ...rest } = parsed;
    const s = JSON.stringify(rest);
    if (s === "{}" || s === "null") return null;
    return s.length > 100 ? s.slice(0, 100) + "…" : s;
  } catch {
    return null;
  }
}
