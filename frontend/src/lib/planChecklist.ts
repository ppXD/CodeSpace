/// The plan-checklist render vocabulary — the ONE place the backend's open `WorkPlanItemStates` strings map to
/// icon / tone / word, so the row renderer and any future consumer (run-detail plan panel) can't drift. Unknown
/// states degrade NEUTRAL (never red): the vocabulary is open by contract, and a new backend state must read as
/// "something new", not "broken".

export function planStateIcon(state: string): string {
  if (state === "Completed") return "square-check";
  if (state === "Failed") return "square-x";
  if (state === "NeedsReview") return "alert";
  if (state === "InProgress") return "dot";
  return "square";
}

export function planStateTone(state: string): string {
  if (state === "Completed") return "ok";
  if (state === "Failed") return "err";
  if (state === "NeedsReview") return "warn";
  if (state === "InProgress") return "run";
  return "idle";
}

export function planStateWord(state: string): string {
  if (state === "Completed") return "done";
  if (state === "Failed") return "failed";
  if (state === "NeedsReview") return "needs review";
  if (state === "InProgress") return "running";
  if (state === "Pending") return "pending";
  return state.toLowerCase();
}

/// The nearest AgentRunStatus name for the agent drawer's header tone — the terminal itself re-reads the live agent.
export function planAgentStatus(state: string): string {
  if (state === "InProgress") return "Running";
  if (state === "Completed") return "Succeeded";
  if (state === "NeedsReview") return "NeedsReview";
  if (state === "Failed") return "Failed";
  return "Queued";
}

/// "after #1, #3" — the dependency ordinals as reader-facing copy. Empty input → null (no chip).
export function planDepsLabel(deps: number[] | null | undefined): string | null {
  if (!deps || deps.length === 0) return null;
  return `after ${deps.map((d) => `#${d}`).join(", ")}`;
}

/// Compose the confirmation answer's feedback text from the operator's question choices + free-text note —
/// one "question → choice" line per answered question, then the note. Empty when nothing was entered (the
/// caller decides whether that's allowed — approve tolerates it, request-changes requires it).
export function composePlanFeedback(choices: Array<{ question: string; choice: string }>, note: string): string {
  const lines = choices.map((c) => `${c.question} → ${c.choice}`);
  const text = note.trim();
  if (text) lines.push(text);
  return lines.join("\n");
}
