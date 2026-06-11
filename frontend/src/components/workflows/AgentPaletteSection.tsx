import { Ic } from "@/_imported/ai-code-space/icons";
import { useAgentDefinitions } from "@/hooks/use-agents";

export interface AgentPaletteSectionProps {
  /** Only rendered when the agent.code node type is loaded — it's what a dragged agent materializes into. */
  enabled: boolean;
  /** Add a Run-coding-agent node pre-bound to this persona id. */
  onAdd: (agentDefinitionId: string) => void;
}

/** The dataTransfer key a dragged Agent palette item carries — read by the canvas drop handler. */
export const AGENT_DRAG_MIME = "application/x-workflow-agent";

/**
 * "Agents" palette section — lists the team's Agent personas as draggable items next to the generic
 * node Steps. Picking or dragging one drops a Run-coding-agent (agent.code) node pre-bound to that
 * persona, so its prompt + model become the run's defaults; the generic "Run coding agent" node stays
 * in Steps for inline runs. Hidden entirely when no agent.code node is registered or the team has no
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
      {rows.map((a) => (
        <button
          key={a.id}
          type="button"
          className="wf-palette-item"
          draggable
          onClick={() => onAdd(a.id)}
          onDragStart={(e) => { e.dataTransfer.setData(AGENT_DRAG_MIME, a.id); e.dataTransfer.effectAllowed = "move"; }}
          title={`Add a Run coding agent step bound to @${a.slug}\nClick to add · Drag to position`}
        >
          <span className="wf-palette-item-icon"><Ic.Bot size={16} /></span>
          <span className="wf-palette-item-body">
            <span className="wf-palette-item-name">{a.name || `@${a.slug}`}</span>
            <span className="wf-palette-item-key">@{a.slug}</span>
          </span>
          <span className="wf-palette-item-add" aria-hidden>+</span>
        </button>
      ))}
    </div>
  );
}
