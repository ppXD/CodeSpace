import { memo } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PendingDecision } from "@/api/workflows";

import { DecisionCard } from "./DecisionCard";

/**
 * The Run Room's decision inbox — the "what needs you NOW" surface in the context rail. It lists this run's pending
 * decisions (already narrowed to the run tree by the caller) as answerable cards. When nothing is parked it shows a
 * calm all-clear, so the rail reads intentional rather than empty. Loading is the caller's concern (it owns the query).
 */
// Memoized: the route now passes a referentially-stable `decisions` array (useMemo), so the 2s/3s run + decision polls
// don't re-render the inbox (and every DecisionCard) when the pending set didn't change.
export const DecisionInbox = memo(function DecisionInbox({ decisions }: { decisions: readonly PendingDecision[] }) {
  return (
    <section className="decision-inbox rail-card" aria-label="Decisions">
      <div className="decision-inbox-head">
        <Ic.Bell size={12} aria-hidden="true" />
        <span className="decision-inbox-title">Needs you</span>
        {decisions.length > 0 && (
          <span className="decision-inbox-count" aria-label={`${decisions.length} pending`}>{decisions.length}</span>
        )}
      </div>

      {decisions.length === 0 ? (
        <div className="decision-inbox-clear"><Ic.Check size={12} /> All clear — nothing needs a decision.</div>
      ) : (
        <div className="decision-inbox-list">
          {decisions.map((d) => <DecisionCard key={d.id} decision={d} />)}
        </div>
      )}
    </section>
  );
});
