import { useMemo } from "react";

import { useRunPhases, useRunTimeline } from "@/hooks/use-workflows";

import { ActivityEventRow } from "./RunActivityEventRow";
import { RunActivityFold } from "./RunActivityFold";
import { TimelinePhase } from "./TimelinePhase";
import { buildWaves, composeActivity } from "./runActivity";

/**
 * The Activity tab — the run's execution STORY as ONE chronological rail you scroll from start to end: narrative
 * milestone events, each phase's agents as an inline TimelinePhase (a single-agent run is one terminal; a multi-agent
 * phase is a marker + tiles), and runs of structural detail events folded behind a "N steps" disclosure (see
 * <c>composeActivity</c>). Source-agnostic — Fast / Standard / Deep all flow through one path. The outline drives it:
 * selecting a phase / agent scrolls the rail to it. The full raw audit lives in the Trace tab; both queries poll in
 * lockstep while the run is live.
 */
export function RunActivityTimeline({ runId, selectedPhaseId, selectedAgentRunId, onSelectAgent, rerunnableNodeIds }: {
  runId: string;
  selectedPhaseId?: string | null;
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string | null) => void;
  rerunnableNodeIds?: ReadonlySet<string>;
}) {
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
              <div className="run-activity-wavewrap">
                <TimelinePhase wave={item.wave} selectedPhaseId={selectedPhaseId} selectedAgentRunId={selectedAgentRunId} onSelectAgent={onSelectAgent} rerunnableNodeIds={rerunnableNodeIds} />
              </div>
            </li>
          );
        }

        if (item.kind === "fold") return <RunActivityFold key={item.key} events={item.events} />;

        return <ActivityEventRow key={item.key} event={item.event} />;
      })}
    </ol>
  );
}
