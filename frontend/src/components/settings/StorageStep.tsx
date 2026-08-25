import { useState, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

/**
 * One step of the Settings → Storage flow.
 *
 * <p>`locked` is reserved for a precondition the SERVER refuses on — today only "a data route may
 * only target an Active storage profile" (`StorageRouteService.RequireActiveProfileAsync`). A step
 * that is merely later in the order is `upcoming`: nothing refuses it, so it keeps its control and
 * simply loses the accent. Marking such a step locked would state a rule that does not exist.</p>
 *
 * <p>Only the `active` step carries a primary button, so the screen has at most one at a time.</p>
 */
export type StorageStepState = "done" | "active" | "upcoming" | "locked";

interface StorageStepProps {
  /** Stable identity of the step, independent of its title. */
  step: string;
  title: string;
  titleId: string;
  state: StorageStepState;
  /** One line naming what this step's state is right now. */
  line: ReactNode;
  /** `locked` only: the precondition, stated where the action would otherwise sit. */
  precondition?: string;
  action?: ReactNode;
  children?: ReactNode;
}

export function StorageStep({ step, title, titleId, state, line, precondition, action, children }: StorageStepProps) {
  const [expanded, setExpanded] = useState(false);
  const body = state === "locked" ? null : children;
  // A finished step collapses to its summary line; everything still to do stays open, because that is
  // where the operator is working.
  const open = state !== "done" || expanded;

  return (
    <section className="stg-step" data-step={step} data-step-state={state} aria-labelledby={titleId}>
      <div className="stg-rail" aria-hidden="true">
        <span className="stg-marker">{markerGlyph(state)}</span>
        <span className="stg-rail-line" />
      </div>
      <div className="stg-main">
        <div className="stg-head">
          <div className="stg-headings">
            <h3 className="stg-title" id={titleId}>{title}</h3>
            <div className="stg-line">{state === "locked" ? `Available once ${precondition}` : line}</div>
          </div>
          <div className="stg-actions">
            {state === "done" && body != null && (
              <button type="button" className="btn btn-ghost stg-toggle" aria-expanded={expanded} aria-label={`${expanded ? "Hide" : "Show"} ${title}`} onClick={() => setExpanded(!expanded)}>
                {expanded ? "Hide" : "Show"}
              </button>
            )}
            {state !== "locked" && action}
          </div>
        </div>
        {open && body != null && <div className="stg-body">{body}</div>}
      </div>
    </section>
  );
}

function markerGlyph(state: StorageStepState): ReactNode {
  if (state === "done") return <Ic.Check size={12} />;
  if (state === "locked") return <Ic.Lock size={11} />;
  // A filled span rather than `Ic.Dot`, which wraps lucide's Circle at strokeWidth 0 and so paints nothing.
  return <span className="stg-marker-dot" />;
}
