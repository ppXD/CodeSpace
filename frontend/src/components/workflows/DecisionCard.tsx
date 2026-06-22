import { useEffect, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { AnswerDecisionInput, DecisionOption, PendingDecision } from "@/api/workflows";
import { ApiError } from "@/api/request";
import { useAnswerDecision } from "@/hooks/use-workflows";

import { deadlineLabel, isSingleChoice } from "./runDecisions";

/**
 * One answerable card in the Run Room's decision inbox — the generic surface over the cross-grain queue. It renders
 * any decision shape (confirm / choose_one / choose_many / free_text / approve_action) and posts the answer to the
 * unified endpoint, which routes to the right durable resume (an agent's mid-run call unblocks, or a node's run
 * resumes). Single-choice shapes decide on one click; choose_many / free_text compose a draft, then Submit. A
 * side-effecting / high-risk option is marked so the irreversible weight is visible before the click.
 */
export function DecisionCard({ decision }: { decision: PendingDecision }) {
  const answer = useAnswerDecision();
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);

  // A live-ish countdown to the bounded deadline — the clock is seeded once (a lazy initializer, so render
  // stays pure) and re-ticked every 30s so "5m left" decays toward "due now" without a manual refresh.
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 30_000);
    return () => clearInterval(t);
  }, []);

  const single = isSingleChoice(decision.decisionType);
  const deadline = deadlineLabel(decision.deadlineAt, nowMs);

  const send = (body: AnswerDecisionInput) => {
    setError(null);
    answer.mutate({ decisionId: decision.id, body }, {
      onError: (e) => {
        // AlreadyResolved / NotFound are benign races — the queue refetch (onSettled) drops the card. Only a
        // genuinely Invalid answer is worth surfacing inline so the operator can correct + retry.
        const body = e instanceof ApiError ? (e.body as { outcome?: string; message?: string } | undefined) : undefined;
        if (body?.outcome === "Invalid") setError(body.message ?? "That answer doesn’t fit this decision.");
      },
    });
  };

  const toggle = (id: string) => setSelected((prev) => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });

  const submitMany = () => send({ selectedOptions: [...selected] });
  const submitText = () => send({ freeText: text.trim() });

  const busy = answer.isPending;

  // The affordance is driven by the shape AND whether options are present: an options-less decision (e.g. a
  // `confirm` node with no author-supplied choices) is answered with free text — the backend validator accepts
  // exactly that when Options is empty, and projecting buttons over an empty list would render a dead-end card.
  const hasOptions = decision.options.length > 0;
  const mode = single && hasOptions ? "choose-one"
    : decision.decisionType === "choose_many" && hasOptions ? "choose-many"
      : "free-text";

  return (
    <div className="decision-card" data-risk={decision.riskLevel.toLowerCase()}>
      <div className="decision-card-head">
        <span className="decision-card-type">{decisionTypeLabel(decision.decisionType)}</span>
        {decision.riskLevel.toLowerCase() === "high" && (
          <span className="decision-card-risk"><Ic.Triangle size={10} /> high risk</span>
        )}
        {deadline && <span className="decision-card-deadline"><Ic.Clock size={10} /> {deadline}</span>}
      </div>

      <div className="decision-card-q">{decision.question}</div>

      {decision.contextSummary && <div className="decision-card-context">{decision.contextSummary}</div>}
      {decision.blockingReason && <div className="decision-card-blocked">Blocked: {decision.blockingReason}</div>}

      {mode === "choose-one" ? (
        <div className="decision-card-options">
          {decision.options.map((o) => (
            <OptionButton key={o.id} option={o} recommended={o.id === decision.recommendedOption} disabled={busy}
              onClick={() => send({ selectedOptions: [o.id] })} />
          ))}
        </div>
      ) : mode === "choose-many" ? (
        <>
          <ul className="decision-card-checks">
            {decision.options.map((o) => (
              <li key={o.id}>
                <label className="decision-card-check">
                  <input type="checkbox" checked={selected.has(o.id)} onChange={() => toggle(o.id)} disabled={busy} />
                  <span>{o.label}</span>
                  {o.isSideEffecting && <Ic.Triangle size={10} aria-label="side-effecting" />}
                </label>
              </li>
            ))}
          </ul>
          <div className="decision-card-actions">
            <button className="btn btn-primary decision-card-submit" onClick={submitMany} disabled={busy || selected.size === 0}>Submit</button>
          </div>
        </>
      ) : (
        <div className="decision-card-free">
          <textarea className="decision-card-textarea" value={text} onChange={(e) => setText(e.target.value)}
            placeholder="Type your answer…" disabled={busy} rows={2} />
          <div className="decision-card-actions">
            <button className="btn btn-primary decision-card-submit" onClick={submitText} disabled={busy || text.trim() === ""}>Submit</button>
          </div>
        </div>
      )}

      {error && <div className="decision-card-error" role="alert"><Ic.Triangle size={11} /> {error}</div>}
    </div>
  );
}

/** One option of a single-choice decision — a side-effecting choice carries a danger tone + warning glyph. */
function OptionButton({ option, recommended, disabled, onClick }: { option: DecisionOption; recommended: boolean; disabled: boolean; onClick: () => void }) {
  return (
    <button
      className={`btn decision-card-option${option.isSideEffecting ? " btn-danger" : " btn-primary"}`}
      data-recommended={recommended || undefined}
      disabled={disabled}
      onClick={onClick}
    >
      {option.isSideEffecting && <Ic.Triangle size={11} />}
      {option.label}
      {recommended && <span className="decision-card-rec">recommended</span>}
    </button>
  );
}

/** A friendly label for the known decision shapes; an unknown type falls back to its raw token. */
function decisionTypeLabel(type: string): string {
  switch (type) {
    case "confirm": return "Confirm";
    case "choose_one": return "Choose one";
    case "choose_many": return "Choose any";
    case "free_text": return "Answer";
    case "approve_action": return "Approval";
    default: return type;
  }
}
