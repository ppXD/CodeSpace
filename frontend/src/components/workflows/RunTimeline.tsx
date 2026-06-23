import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunTimelineEvent } from "@/api/workflows";
import { useRunTimeline } from "@/hooks/use-workflows";

/**
 * The run's narrative timeline — the merged event story (GET /timeline): the "what happened, in order" line of the
 * command-center Activity. ONE chronological list of meaningful events (run/node lifecycle + the agents' file edits,
 * test output, errors, and final summary), each toned by its severity. Source-agnostic — it never switches on the
 * event's open `kind`, only on `severity`. The raw per-event detail + the full unfiltered audit stay in the agent
 * cards and the (future) Trace tab. Polls in lockstep with the run; renders nothing until the run produces events.
 */
export function RunTimeline({ runId }: { runId: string }) {
  const timeline = useRunTimeline(runId);
  const events = timeline.data?.events ?? [];

  if (events.length === 0) return null;

  return (
    <section className="run-timeline">
      <div className="run-timeline-head"><Ic.Clock size={12} aria-hidden="true" /> Timeline</div>
      <ol className="run-timeline-list">
        {events.map((e) => <TimelineRow key={e.id} event={e} />)}
      </ol>
    </section>
  );
}

function TimelineRow({ event }: { event: RunTimelineEvent }) {
  return (
    <li className="run-timeline-event" data-severity={event.severity.toLowerCase()}>
      <span className="run-timeline-time">{new Date(event.occurredAt).toLocaleTimeString()}</span>
      <span className="run-timeline-dot" aria-hidden="true" />
      <div className="run-timeline-body">
        <span className="run-timeline-title">{event.title}</span>
        {event.summary && <span className="run-timeline-summary">{event.summary}</span>}
      </div>
    </li>
  );
}
