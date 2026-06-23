import { useMemo } from "react";

import type { RunTimelineEvent } from "@/api/workflows";
import { useRunPhases, useRunTimeline } from "@/hooks/use-workflows";

import { AgentWave } from "./AgentWave";
import { buildWaves, mergeActivityStream } from "./runActivity";

/**
 * The Activity tab — the run's live execution STORY as ONE chronological stream you scroll. It merges the run's two
 * read planes: the narrative timeline events (lifecycle / agent edits / supervisor decisions, from <c>/timeline</c>)
 * and the phase tree's agent groupings (from <c>/phases</c>), interleaving each agent WAVE as an inline block at its
 * spawn position among the events (see <c>mergeActivityStream</c>). Source-agnostic — a single-agent run, a map
 * fan-out, and a supervisor wave all flow through the same path. The lightweight run-state summary lives in the page
 * header; the full raw audit lives in the Trace tab. Polls in lockstep (both queries refetch while the run is live).
 */
export function RunActivityTimeline({ runId, selectedAgentRunId }: { runId: string; selectedAgentRunId?: string | null }) {
  const timeline = useRunTimeline(runId);
  const phases = useRunPhases(runId);

  const eventsData = timeline.data?.events;
  const phasesData = phases.data?.phases;

  const waves = useMemo(() => buildWaves(phasesData ?? []), [phasesData]);
  const items = useMemo(() => mergeActivityStream(eventsData ?? [], waves), [eventsData, waves]);

  if (items.length === 0) {
    const loading = (timeline.isLoading && !eventsData) || (phases.isLoading && !phasesData);
    return <div className="run-activity-empty">{loading ? "Loading the run…" : "No activity yet."}</div>;
  }

  return (
    <ol className="run-activity-list">
      {items.map((item) => item.kind === "wave"
        ? (
          <li key={item.key} className="run-activity-row run-activity-waverow">
            <span className="run-activity-time">{item.at ? new Date(item.at).toLocaleTimeString() : ""}</span>
            <span className="run-activity-ring" aria-hidden="true"></span>
            <div className="run-activity-wavewrap"><AgentWave wave={item.wave} selectedAgentRunId={selectedAgentRunId} /></div>
          </li>
        )
        : <ActivityEventRow key={item.key} event={item.event} />)}
    </ol>
  );
}

function ActivityEventRow({ event }: { event: RunTimelineEvent }) {
  return (
    <li className="run-activity-row run-activity-event" data-severity={event.severity.toLowerCase()}>
      <span className="run-activity-time">{new Date(event.occurredAt).toLocaleTimeString()}</span>
      <span className="run-activity-dot" aria-hidden="true"></span>
      <div className="run-activity-body">
        <span className="run-activity-title">{event.title}</span>
        {event.summary && <span className="run-activity-summary">{event.summary}</span>}
      </div>
    </li>
  );
}
