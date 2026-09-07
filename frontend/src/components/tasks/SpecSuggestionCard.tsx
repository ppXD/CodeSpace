import type { TaskAcceptanceCompatibility, TaskSpecModelCall, TaskSpecRepositoryObservation, TaskSpecSuggestion } from "@/api/tasks";
import { Ic } from "@/_imported/ai-code-space/icons";
import { adoptableSpecChecks, sourceSupportedSpecChecks } from "@/lib/specProposal";

export function SpecSuggestionCard({ suggestion, repositoryObservation, acceptanceCompatibility, applied, onApplyChecks, onApplyCriteria, onDismiss }: {
  suggestion: TaskSpecSuggestion;
  repositoryObservation?: TaskSpecRepositoryObservation | null;
  acceptanceCompatibility?: TaskAcceptanceCompatibility | null;
  applied: { checks: boolean; criteria: boolean };
  onApplyChecks: () => void;
  onApplyCriteria: () => void;
  onDismiss: () => void;
}) {
  const proposal = suggestion.acceptanceProposal;
  const argv = proposal?.argv ?? suggestion.acceptanceChecks;
  const hasProposal = argv.length > 0;
  const sourceSupported = sourceSupportedSpecChecks(suggestion).length > 0;
  const hasChecks = adoptableSpecChecks(suggestion, acceptanceCompatibility).length > 0;
  const noChecks = !hasProposal;
  const hasCriteria = suggestion.acceptanceCriteria.length > 0;
  const allApplied = (!hasChecks || applied.checks) && (!hasCriteria || applied.criteria);
  const band = suggestion.confidence >= 0.75 ? "high" : suggestion.confidence >= 0.5 ? "mid" : "low";
  const observation = repositoryObservation?.state;
  const repositoryLabel = observation === "ObservedEmpty" ? "Repository root observed empty" : observation === "Observed" ? "Repository read at a fixed commit" : observation === "NotRequested" ? "No repository requested" : observation === "Unavailable" ? "Repository evidence unavailable" : "Repository observation unknown";
  const sourceLabel = proposal?.source === "user-explicit" ? "User-requested command" : proposal?.source === "repository-evidence" ? "Repository-supported proposal" : "Unverified proposal";
  if (!hasProposal && !hasCriteria) return null;
  return (
    <div className="lt3-spec" data-testid="spec-suggestion-card">
      <div className="lt3-spec-h">
        <Ic.Sparkles size={14} />
        <span>Suggested contract</span>
        <span className="lt3-spec-badge" data-band={band} title="The proposing model's confidence; not an execution result">{Math.round(suggestion.confidence * 100)}% confident</span>
        <span className="lt3-spec-badge" data-warn={observation !== "Observed" && observation !== "ObservedEmpty"} title={repositoryObservation?.detail}>{repositoryLabel}</span>
        <button type="button" className="lt3-spec-x" aria-label="Dismiss suggestion" onClick={onDismiss}><Ic.X size={13} /></button>
      </div>
      {hasProposal && (
        <div className="lt3-spec-row">
          <span className="lt3-spec-l">Checks</span>
          <span className="lt3-spec-v">
            <span className="lt3-spec-cmd">{argv.map((token, i) => <code key={i} className="lt3-spec-chip">{JSON.stringify(token)}</code>)}</span>
            <span className="lt3-spec-sub">{sourceLabel} · {sourceSupported ? "Source assessed; not executed" : proposal?.status === "Contradicted" ? "Conflicting evidence; not applied" : "Evidence unknown; not applied"}</span>
            <span className="lt3-spec-sub">{hasChecks ? "Applying makes this an acceptance requirement: it must exit 0 after the work." : "Review the evidence and dependencies before adding a command under Evaluation. You can continue with the task's content criteria."}{applied.checks && <span className="lt3-spec-went"> · filled into Evaluation → Acceptance checks</span>}</span>
          </span>
          {hasChecks && <button type="button" className="lt3-spec-apply" disabled={applied.checks} title="Fills Evaluation → Acceptance checks (editable there)" onClick={onApplyChecks}>{applied.checks ? <><Ic.Check size={11} /> Applied</> : "Apply"}</button>}
        </div>
      )}
      {noChecks && <div className="lt3-spec-row"><span className="lt3-spec-l">Checks</span><span className="lt3-spec-v lt3-spec-none">None suggested — the model's note below says why. Add your own under Evaluation if you know the command.</span></div>}
      {hasProposal && <div className="lt3-spec-note" data-testid="spec-execution-compatibility">{acceptanceCompatibility?.detail ?? "Execution adapter compatibility is unknown. The proposal cannot be added as a mandatory check."}</div>}
      {hasProposal && <div className="lt3-spec-note">{proposal?.reason ?? "The server did not provide a source assessment for this legacy suggestion; the command remains unverified."}</div>}
      {hasProposal && proposal && (proposal.evidence?.length > 0 || proposal.dependencies?.length > 0) && (
        <details className="lt3-spec-note">
          <summary>Evidence and dependency checks</summary>
          {proposal.evidence?.map((evidence, i) => <div key={`${evidence.sourceId}-${i}`}><div>{evidence.path ?? "Original user goal"}{evidence.reference && <> · <code>{evidence.reference}</code></>}</div><blockquote>{evidence.quote}</blockquote><div>Content digest: <code>{evidence.contentDigest}</code></div></div>)}
          {proposal.dependencies?.map((dependency, i) => <div key={i}><strong>{dependency.requirement}</strong><p>Proposed check: {dependency.validationStrategy}</p></div>)}
          <p>These excerpts identify sources. Their meaning was assessed by a model; no command execution or permission is established here.</p>
        </details>
      )}
      {hasCriteria && (
        <div className="lt3-spec-row">
          <span className="lt3-spec-l">Criteria</span>
          <span className="lt3-spec-v">
            <ul className="lt3-spec-list">{suggestion.acceptanceCriteria.map((criterion, i) => <li key={i}>{criterion}</li>)}</ul>
            <span className="lt3-spec-sub">Steers the work — rendered into the agent's brief{applied.criteria && <span className="lt3-spec-went"> · filled into Evaluation → Acceptance criteria</span>}</span>
          </span>
          <button type="button" className="lt3-spec-apply" disabled={applied.criteria} title="Fills Evaluation → Acceptance criteria (editable there)" onClick={onApplyCriteria}>{applied.criteria ? <><Ic.Check size={11} /> Applied</> : "Apply"}</button>
        </div>
      )}
      {suggestion.rationale && <div className="lt3-spec-note">{suggestion.rationale}</div>}
      <div className="lt3-spec-f">
        <span className="lt3-spec-r">Applied fields stay editable · unverified commands are excluded</span>
        <button type="button" className="lt3-spec-all" disabled={allApplied} onClick={() => { if (hasChecks) onApplyChecks(); if (hasCriteria) onApplyCriteria(); }}>Apply all</button>
      </div>
    </div>
  );
}

export function SpecModelCalls({ calls }: { calls: TaskSpecModelCall[] | null | undefined }) {
  if (!calls?.length) return null;
  const incomplete = calls.some(call => call.usageMayBeIncomplete || call.inputTokens == null || call.outputTokens == null);
  return (
    <details className="lt3-spec-note" data-testid="spec-model-calls">
      <summary>Spec preview: {calls.length} model {calls.length === 1 ? "stage" : "stages"}{incomplete ? " · usage partly unknown" : " · usage reported"}</summary>
      {calls.map((call, i) => <div key={i}><strong>{call.phase === "semantic-review" ? "Source review" : "Proposal"}: {call.outcome}</strong><div>Selected: {call.selectedModel ?? "unavailable"} · Answered: {call.actualModel ?? "unknown"}</div><div>Input tokens: {call.inputTokens ?? "unknown"} · Output tokens: {call.outputTokens ?? "unknown"}</div>{call.failedOver?.map((hop, j) => <div key={j}>Failover: {hop}</div>)}</div>)}
      {incomplete && <p>Missing usage is not zero cost. Failed or timed-out attempts may still be billed.</p>}
    </details>
  );
}
