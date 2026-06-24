import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunTimelineEvent } from "@/api/workflows";

import { ActivityEventRow } from "./RunActivityEventRow";

/**
 * A folded run of consecutive DETAIL events — the structural churn (node started/completed, file edits) tucked behind
 * one "N steps" disclosure so the story stays milestones + waves. Collapsed by default; the caret reveals the detail
 * rows inline (dimmed, via their own `data-detail`). The full raw form always lives in the Trace tab.
 */
export function RunActivityFold({ events }: { events: RunTimelineEvent[] }) {
  const [open, setOpen] = useState(false);
  const n = events.length;

  return (
    <>
      <li className="run-activity-row run-activity-fold">
        <span className="run-activity-time"></span>
        <span className="run-activity-dot run-activity-fold-dot" aria-hidden="true"></span>
        <button type="button" className="run-activity-foldbtn" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
          <Ic.ChevronRight size={12} aria-hidden="true" />
          {n} {n === 1 ? "step" : "steps"}
        </button>
      </li>
      {open && events.map((e) => <ActivityEventRow key={e.id} event={e} />)}
    </>
  );
}
