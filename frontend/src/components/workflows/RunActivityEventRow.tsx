import type { RunTimelineEvent } from "@/api/workflows";

/**
 * One narrative event on the activity rail — toned PURELY off the closed `severity` axis (never the open `kind`). A
 * `Detail`-level event (a lone one the composer didn't fold, or a row revealed inside a fold) reads `data-detail` so
 * it dims back from the milestones. Shared by the timeline + the fold disclosure.
 */
export function ActivityEventRow({ event }: { event: RunTimelineEvent }) {
  return (
    <li className="run-activity-row run-activity-event" data-severity={event.severity.toLowerCase()} data-detail={event.level === "Detail" || undefined}>
      <span className="run-activity-time">{new Date(event.occurredAt).toLocaleTimeString()}</span>
      <span className="run-activity-dot" aria-hidden="true"></span>
      <div className="run-activity-body">
        <span className="run-activity-title">{event.title}</span>
        {event.summary && <span className="run-activity-summary">{event.summary}</span>}
      </div>
    </li>
  );
}
