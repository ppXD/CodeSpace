import { Ic } from "@/_imported/ai-code-space/icons";
import { useAgentDefinitions } from "@/hooks/use-agents";

export interface AgentPaletteSectionProps {
  /** Only rendered when the agent.run node type is loaded — it's what a dragged agent materializes into. */
  enabled: boolean;
  /** Add a Run-coding-agent node pre-bound to this persona id. */
  onAdd: (agentDefinitionId: string) => void;
}

/** The dataTransfer key a dragged Agent palette item carries — read by the canvas drop handler. */
export const AGENT_DRAG_MIME = "application/x-workflow-agent";

/**
 * "Agents" palette section — lists the team's Agent personas as draggable items next to the generic
 * node Steps. Picking or dragging one drops a Run-agent (agent.run) node pre-bound to that
 * persona, so its prompt + model become the run's defaults; the generic "Run agent" node stays
 * in Steps for inline runs. Hidden entirely when no agent.run node is registered or the team has no
 * personas yet.
 */
export function AgentPaletteSection({ enabled, onAdd }: AgentPaletteSectionProps) {
  const agents = useAgentDefinitions();

  if (!enabled) return null;

  const rows = agents.data ?? [];
  if (rows.length === 0) return null;

  return (
    <div className="wf-palette-section">
      <div className="wf-palette-section-h">Agents</div>
      <div className="wf-palette-grid">
        {rows.map((a) => (
          <button
            key={a.id}
            type="button"
            className="wf-palette-item"
            data-tone="ai"
            draggable
            onClick={() => onAdd(a.id)}
            onDragStart={(e) => { e.dataTransfer.setData(AGENT_DRAG_MIME, a.id); e.dataTransfer.effectAllowed = "move"; }}
            title={`Add a Run agent step bound to @${a.slug}\nClick to add · Drag to position`}
          >
            {/* Same centred-tile shape as the node tiles; agents carry no effect tags, but the reserved
                tag row keeps them one height with the rest of the grid. */}
            <span className="wf-palette-item-icon"><Ic.Bot size={16} /></span>
            <span className="wf-palette-item-name">{a.name || `@${a.slug}`}</span>
            <span className="wf-palette-item-key">@{a.slug}</span>
            <span className="wf-palette-tile-tags" />
          </button>
        ))}
      </div>
    </div>
  );
}
