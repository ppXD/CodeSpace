import { useMemo } from "react";

import { useRunPhases, useRunTimeline } from "@/hooks/use-workflows";

import { AgentWave } from "./AgentWave";
import { ActivityEventRow } from "./RunActivityEventRow";
import { RunActivityFold } from "./RunActivityFold";
import { buildWaves, composeActivity } from "./runActivity";

/**
 * The Activity tab — the run's live execution STORY as ONE chronological stream you scroll. It composes the run's two
 * read planes: the narrative timeline events (lifecycle / agent edits / supervisor decisions, from <c>/timeline</c>)
 * and the phase tree's agent groupings (from <c>/phases</c>), interleaving each agent WAVE as an inline FLEET CARD at
 * its spawn position and folding runs of structural DETAIL events behind a "N steps" disclosure (see
 * <c>composeActivity</c>), so the story reads as milestones + waves. Source-agnostic — a single-agent run, a map
 * fan-out, and a supervisor wave all flow through the same path. The full raw audit lives in the Trace tab. Polls in
 * lockstep (both queries refetch while the run is live).
 */
export function RunActivityTimeline({ runId, selectedAgentRunId }: { runId: string; selectedAgentRunId?: string | null }) {
  const timeline = useRunTimeline(runId);
  const phases = useRunPhases(runId);

  const eventsData = timeline.data?.events;
  const phasesData = phases.data?.phases;

  const waves = useMemo(() => buildWaves(phasesData ?? []), [phasesData]);
  const items = useMemo(() => composeActivity(eventsData ?? [], waves), [eventsData, waves]);

  if (items.length === 0) {
    const loading = (timeline.isLoading && !eventsData) || (phases.isLoading && !phasesData);
    return <div className="run-activity-empty">{loading ? "Loading the run…" : "No activity yet."}</div>;
  }

  return (
    <ol className="run-activity-list">
      {items.map((item) => {
        if (item.kind === "wave") {
          return (
            <li key={item.key} className="run-activity-row run-activity-waverow">
              <span className="run-activity-time">{item.at ? new Date(item.at).toLocaleTimeString() : ""}</span>
              <span className="run-activity-ring" aria-hidden="true"></span>
              <div className="run-activity-wavewrap"><AgentWave wave={item.wave} selectedAgentRunId={selectedAgentRunId} /></div>
            </li>
          );
        }

        if (item.kind === "fold") return <RunActivityFold key={item.key} events={item.events} />;

        return <ActivityEventRow key={item.key} event={item.event} />;
      })}
    </ol>
  );
}
