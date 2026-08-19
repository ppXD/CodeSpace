import type { JournalStep, JournalTurn, JournalView } from "@/api/sessions";

/// The consumer of the backend's `?since=` journal delta (`JournalDelta.After`). A live journal poll would otherwise
/// re-send the whole session — every turn's full step walk — every 2s per viewer, growing with the session rather than
/// with what changed. The delta trims what the client provably already holds, and this merges it back.
///
/// Returns the reconciled view, or `null` meaning "my accumulation diverged from the server's — re-fetch in FULL".
/// That is not an error path; it is the delta's correctness backstop, and the only reason trimming is safe. Every turn
/// carries `stepCount` — the server's untrimmed total — so a client that ends up with a different number of steps knows
/// it is wrong even though nothing failed. That happens on an out-of-order backfill (a step landing below the cursor,
/// which an append-only delta cannot carry), on a step that vanished, and on a non-focused turn that finished between
/// two polls and gained steps this client never saw. The caller must REPLACE its accumulation with a full fetch rather
/// than merge into it: a full fetch is authoritative, so the mismatch cannot recur from the same cause. Worst case this
/// degrades to the full poll it replaced — it never loses a step.
export function mergeJournalDelta(prior: JournalView, delta: JournalView): JournalView | null {
  const carried = new Map(prior.turns.map((turn) => [turnKey(turn), turn.steps]));
  const turns: JournalTurn[] = [];

  for (const turn of delta.turns) {
    const steps = upsertById(carried.get(turnKey(turn)) ?? [], turn.steps);

    if (steps.length !== turn.stepCount) return null;

    turns.push({ ...turn, steps });
  }

  return { ...delta, turns };
}

/// Keyed by turn AND run: a rerun gives the same turn index a new run, and the failed attempt's steps are not the new
/// attempt's. Keying on the index alone would carry the old attempt's walk into the new one.
function turnKey(turn: JournalTurn): string {
  return `${turn.turnIndex}:${turn.runId}`;
}

/// Append what arrived, dropping any copy of it we already held. Appending is the right order because the delta only
/// ever delivers steps the server sorted AFTER our cursor; a step re-delivered under an id we already have is one whose
/// own timestamp moved forward (an in-flight beat that resolved), so the end is where it now belongs. Any case where
/// that is not true shows up in the caller's count check as a divergence and is repaired by a full fetch.
function upsertById(held: JournalStep[], arrived: JournalStep[]): JournalStep[] {
  if (arrived.length === 0) return held;

  const arrivedIds = new Set(arrived.map((step) => step.id));

  return [...held.filter((step) => !arrivedIds.has(step.id)), ...arrived];
}
