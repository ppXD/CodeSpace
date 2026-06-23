import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunPhase } from "@/api/workflows";

import { AgentCard } from "./AgentCard";
import { dedupRunAgents } from "./runPhases";

const SUPERVISOR_SOURCE = "supervisor-ledger";
const NODE_SOURCE = "node-summary";

interface LeadStripSpec {
  mode: "supervisor" | "planner" | "nodes";
  title: string;
  detail: string;
}

/**
 * The conditional lead strip — ONE polymorphic slot whose content follows the run's shape (read from the phase
 * tree, never hardcoded per run type): a supervisor strip when the run has a supervisor ledger, a planner strip
 * for a multi-agent fan-out, a node-path strip for a structural workflow, and nothing for a lone agent (its own
 * card leads). Returns null when there's no strip to show.
 */
function leadStrip(phases: readonly RunPhase[], agentCount: number): LeadStripSpec | null {
  if (phases.some((p) => p.sourceKey === SUPERVISOR_SOURCE)) {
    const active = phases.find((p) => p.status === "Active");
    return { mode: "supervisor", title: "Supervisor", detail: active?.label ? `on ${active.label}` : "coordinating agents" };
  }

  if (agentCount > 1) {
    return { mode: "planner", title: "Planner", detail: `${agentCount} agents in parallel` };
  }

  const nodePath = phases.filter((p) => p.sourceKey === NODE_SOURCE).map((p) => p.label);
  if (agentCount === 0 && nodePath.length > 1) {
    return { mode: "nodes", title: "Workflow", detail: nodePath.slice(0, 5).join(" → ") + (nodePath.length > 5 ? " → …" : "") };
  }

  return null;
}

const STRIP_ICON = { supervisor: Ic.Sparkles, planner: Ic.Fork, nodes: Ic.Workflow };

/**
 * The Live-work band — the heart of the command-center Activity. A conditional lead strip (supervisor / planner /
 * node-path, or none for a single agent) over the run's agent cards. Built purely from the phase projection, so it
 * works for ANY run shape: a single-agent run collapses to one card, a supervisor run expands to a strip + N cards.
 * Returns null when nothing agent-shaped has surfaced yet (the caller then shows the node list instead). The
 * narrative timeline + per-agent rollups (files/tokens/model/round N-M) arrive with the later slices.
 */
export function RunLiveWork({ phases, selectedAgentRunId }: { phases: readonly RunPhase[]; selectedAgentRunId?: string | null }) {
  const agents = dedupRunAgents(phases);
  const strip = leadStrip(phases, agents.length);
  const StripIcon = strip ? STRIP_ICON[strip.mode] : null;

  if (!strip && agents.length === 0) return null;

  return (
    <div className="run-livework">
      {strip && StripIcon && (
        <div className="run-leadstrip" data-mode={strip.mode}>
          <StripIcon size={13} aria-hidden="true" />
          <span className="run-leadstrip-title">{strip.title}</span>
          <span className="run-leadstrip-detail">{strip.detail}</span>
        </div>
      )}

      {agents.length > 0 && (
        <div className="run-agent-cards">
          {agents.map((a) => <AgentCard key={a.agentRunId} agent={a} selected={a.agentRunId === selectedAgentRunId} />)}
        </div>
      )}
    </div>
  );
}
