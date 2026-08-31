import type { ProfileAbandonmentSummary, ProfilePlacementOutcome, ProfilePlacementSummary, ProfilePlacementTotal } from "@/api/storage";
import { useAbandonProfilePlacements, useProfilePlacements, useProfilePlacementTotals } from "@/hooks/use-storage";

/** The states a placement can rest in. Everything else still counts against retirement, so it is still the drain's work. */
const RELEASED = ["Purged", "Deleted"];

/** How many placements one pass asks about. The server clamps this; it is here so the number an operator sees is the number sent. */
const BATCH_SIZE = 50;

/**
 * The way out of a profile whose destination is gone.
 *
 * <p>Retirement is refused while anything is still recorded under the profile, and until now that refusal named a
 * population the operator had no way to see and no way to reduce. The three reads say what is held; the pass closes
 * records the destination itself proves it cannot serve.</p>
 *
 * <p>Its one hard rule: never offer another pass over a population this one CANNOT reduce — which is narrower than
 * a pass that reduced nothing. A live button on a genuinely stuck population is an operator pressing forever; a dead
 * one on a population the next pass would drain is worse, because nothing tells them to press again.</p>
 */
export function StoragePlacementDrain({ profileId, disabled }: { profileId: string; disabled: boolean }) {
  const totals = useProfilePlacementTotals(profileId);
  const placements = useProfilePlacements(profileId);
  const abandon = useAbandonProfilePlacements();
  const pass = abandon.data ?? null;
  const held = heldCount(totals.data);
  const remaining = pass ? pass.remaining : held;
  const verdict = pass ? passVerdict(pass) : OFFERED;

  return (
    <div style={{ borderTop: "1px solid var(--line)", marginTop: 18, paddingTop: 16 }}>
      <div className="wf-form-label" style={{ marginBottom: 8 }}>Stored placements</div>
      <div className="cn-banner-p">
        Retirement is refused while anything is still recorded here. A pass asks the destination about a batch and
        closes only the records it proves it cannot serve.
      </div>

      {totals.isLoading && <div className="cn-banner-p">Counting what this profile holds…</div>}
      {totals.error && <div className="cn-banner cn-banner-err" role="alert"><div className="cn-banner-p">Couldn't count this profile's placements.</div></div>}
      {totals.data && (totals.data.length === 0
        ? <div className="cn-banner-p">This profile holds no placements.</div>
        : <PlacementTotals totals={totals.data} />)}

      {placements.data && placements.data.length > 0 && <PlacementList placements={placements.data} />}
      {placements.hasNextPage && (
        <button type="button" className="btn" disabled={placements.isFetchingNextPage} onClick={() => placements.fetchNextPage()}>Load more placements</button>
      )}

      <div style={{ display: "flex", flexWrap: "wrap", gap: 8, marginTop: 12 }}>
        <button
          type="button"
          className="btn"
          disabled={disabled || abandon.isPending || remaining === 0 || !verdict.canRepeat}
          onClick={() => abandon.mutate({ profileId, input: { batchSize: BATCH_SIZE } })}
        >
          {abandon.isPending ? "Abandoning…" : pass ? "Abandon the next batch" : "Abandon a batch"}
        </button>
      </div>

      {abandon.error && <div className="cn-banner cn-banner-err" role="alert" style={{ marginTop: 10 }}><div className="cn-banner-p">The abandonment pass couldn't be completed. Try again.</div></div>}
      {pass && <PassResult pass={pass} note={verdict.note} />}
    </div>
  );
}

function PlacementTotals({ totals }: { totals: ProfilePlacementTotal[] }) {
  return (
    <ul className="cn-list" aria-label="Placements by state" style={{ marginTop: 8 }}>
      {totals.map((total) => (
        <li className="cn-banner-p" key={total.state}>{`${total.state} — ${total.count.toLocaleString()} · ${formatBytes(total.sizeBytes)}`}</li>
      ))}
    </ul>
  );
}

function PlacementList({ placements }: { placements: ProfilePlacementSummary[] }) {
  return (
    <ul className="cn-list" aria-label="Placements under this profile" style={{ marginTop: 8 }}>
      {placements.map((placement) => (
        <li className="cn-banner-p" key={placement.locationId}>
          <span>{placement.objectKey}</span>
          <span className="room-row-mid"> · </span>
          <span>{`${placement.state} · revision ${placement.profileRevision}`}</span>
        </li>
      ))}
    </ul>
  );
}

function PassResult({ pass, note }: { pass: ProfileAbandonmentSummary; note: string | null }) {
  return (
    <div className="cn-banner" role="status" aria-label="Abandonment pass result" style={{ marginTop: 10 }}>
      <div className="cn-banner-p">
        {`Closed ${pass.abandoned}, still served ${pass.stillServed}, unanswered ${pass.unanswered}. `}
        {pass.remaining === 0 ? "Nothing is left to release." : `${pass.remaining} still to release.`}
      </div>
      {note && <div className="cn-banner-p">{note}</div>}
      {pass.outcomes.length > 0 && <PassOutcomes outcomes={pass.outcomes} />}
    </div>
  );
}

/** What the destination said about each placement — the difference between "still served: 3" and knowing which three. */
function PassOutcomes({ outcomes }: { outcomes: ProfilePlacementOutcome[] }) {
  return (
    <ul className="cn-list" aria-label="What this pass established">
      {outcomes.map((outcome) => (
        <li className="cn-banner-p" key={outcome.locationId}>
          {`${outcome.objectKey} — ${outcomeLabel(outcome.outcome)}${outcome.detail ? ` · ${outcome.detail}` : ""}`}
        </li>
      ))}
    </ul>
  );
}

/** What a pass leaves for the next one: the sentence under its result, and whether another pass is worth offering. */
interface PassVerdict {
  note: string | null;
  canRepeat: boolean;
}

/** Nothing established against repeating — the state before any pass, and every pass that left work it did not reach. */
const OFFERED: PassVerdict = { note: null, canRepeat: true };

/** The server's own word for a claim taken and then lost to another drain, as it arrives on a placement's detail. */
const RACED = "StaleWorker";

/**
 * What this pass established about repeating it.
 *
 * <p>Whether the pass left placements unreached decides it, and it is asked FIRST — a stop is worth repeating only
 * inside that answer. A pass that reached the end of the population with nothing answered puts the same question to
 * the same rows next time, whatever else is true of it, and the breaker stopping it is no exception: the breaker
 * fires after each answer, so a population that fits one batch can trip it on its final row. Reading the stop first
 * kept that control live under a note promising rows the pass never reached, when it had reached all of them.</p>
 *
 * <p>Everything else rotates. The server orders each batch least-recently-touched first precisely so placements that
 * always refuse sort behind the ones a pass never reached, which is why a stop that came BEFORE the end is the one
 * most worth repeating: it stopped in front of the rows that would have drained.</p>
 *
 * <p>A batch whose claims were all held elsewhere is two drains racing — kept apart from a destination fault by the
 * server, and answered by waiting rather than by going to repair something that never spoke. Anything else that
 * answered nothing IS the destination, so that is the last case and it names one: the branches below are exhaustive
 * over what a pass can report, and no outcome falls through to a live control under no note at all.</p>
 */
function passVerdict(pass: ProfileAbandonmentSummary): PassVerdict {
  if (pass.remaining === 0) return { note: null, canRepeat: false };

  if (leftPlacementsUnreached(pass)) return pass.stoppedBy ? breakerStop(pass.stoppedBy) : OFFERED;

  if (pass.unanswered < pass.examined) return OFFERED;

  if (pass.outcomes.some(heldElsewhere))
    return { note: "Another drain is holding some of these placements, so the destination was never asked about them. Try again shortly.", canRepeat: true };

  return { note: "Nothing this pass asked answered, and nothing is ordered behind them. Fix the destination or its credential first, then drain again.", canRepeat: false };
}

/**
 * Whether the pass stopped in front of placements it never asked about.
 *
 * <p>Every placement a pass did not reach is still held afterwards, so more held than examined can ONLY mean rows
 * were left behind it — the one reading that needs no assumption about what the answers were. Its complement plus
 * nothing answered is the converse: no record left the population, so held-no-more-than-examined means the pass
 * asked about every one of them, and that is the shape repeating cannot reduce.</p>
 */
function leftPlacementsUnreached(pass: ProfileAbandonmentSummary): boolean {
  return pass.remaining > pass.examined;
}

/** Reached only from inside {@link leftPlacementsUnreached}, so "rows this one never reached" is a fact when it prints. */
function breakerStop(stoppedBy: string): PassVerdict {
  return { note: `The destination answered ${stoppedBy} for much of the batch, so the pass stopped there. The next pass starts behind those placements, at rows this one never reached.`, canRepeat: true };
}

/**
 * Whether this placement went unanswered because another drain had it, rather than because anything refused it.
 *
 * <p>Both carriers are rows another drain is working: a claim the server found already settled carries no detail at
 * all, and a claim taken and then lost carries the server's own name for that race. Every other detail on an
 * unanswered row came back FROM the destination, and waiting cannot change what it said.</p>
 */
function heldElsewhere(outcome: ProfilePlacementOutcome): boolean {
  return outcome.outcome === "Unanswered" && (outcome.detail == null || outcome.detail === RACED);
}

function outcomeLabel(outcome: ProfilePlacementOutcome["outcome"]): string {
  if (outcome === "Abandoned") return "closed";
  if (outcome === "StillServed") return "still served";
  return "no answer";
}

/** Placements that still count against retirement — the same population the server's guard counts. */
function heldCount(totals?: ProfilePlacementTotal[]): number {
  return (totals ?? []).filter((total) => !RELEASED.includes(total.state)).reduce((sum, total) => sum + total.count, 0);
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
