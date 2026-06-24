import { useMemo } from "react";

import { useRunPhases } from "@/hooks/use-workflows";

import { AgentTerminal } from "./AgentTerminal";
import { AgentTile } from "./AgentTile";
import { buildWaves } from "./runActivity";
import { dedupRunAgents } from "./runPhases";

/**
 * The Activity tab — the run's agents as a flat grid of live terminal TILES (the "what are they outputting" view).
 * It is DRIVEN BY THE OUTLINE: with a phase selected it shows only that phase's agents, otherwise every agent in the
 * run; selecting an agent (from the outline or by clicking a tile) focuses that agent and pops its full terminal below
 * the grid. Tokens / tools / time live on the tile footer + the full terminal, never as a list here — the run's
 * narrative + raw audit live in the Trace tab. Polls in lockstep (the phase query refetches while the run is live).
 */
export function RunActivityTiles({ runId, selectedPhaseId, selectedAgentRunId, onSelectAgent }: {
  runId: string;
  selectedPhaseId?: string | null;
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string | null) => void;
}) {
  const phases = useRunPhases(runId);
  const phasesData = phases.data?.phases;

  const waves = useMemo(() => buildWaves(phasesData ?? []), [phasesData]);

  // The agents to tile: the selected phase's wave, else every agent in the run (deduped across phases).
  const agents = useMemo(() => {
    if (selectedPhaseId) return waves.find((w) => w.id === selectedPhaseId)?.agents ?? [];
    return dedupRunAgents(phasesData ?? []);
  }, [waves, selectedPhaseId, phasesData]);

  if (agents.length === 0) {
    const message = phases.isLoading && !phasesData
      ? "Loading the run…"
      : selectedPhaseId
        ? "This phase has no agents."   // the run HAS agents — they're under other phases
        : "No agents yet — this run hasn’t spawned one.";
    return <div className="run-activity-empty">{message}</div>;
  }

  // The open terminal = the focused agent (from the outline / a tile click), when it's among the shown agents.
  const open = agents.find((a) => a.agentRunId === selectedAgentRunId) ?? null;
  const focus = (id: string) => onSelectAgent?.(id === selectedAgentRunId ? null : id);

  return (
    <div className="run-tiles">
      <div className="run-tiles-grid">
        {agents.map((a) => (
          <AgentTile
            key={a.agentRunId}
            agent={a}
            selected={a.agentRunId === selectedAgentRunId}
            open={a.agentRunId === selectedAgentRunId}
            onOpen={() => focus(a.agentRunId)}
          />
        ))}
      </div>

      {open && <AgentTerminal agent={open} onClose={() => onSelectAgent?.(null)} />}
    </div>
  );
}
