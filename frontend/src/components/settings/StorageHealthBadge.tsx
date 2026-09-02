import type { StorageProfileHealthSummary } from "@/api/storage";
import { timeAgo as when } from "@/lib/relativeTime";

/**
 * Whether a destination is taking bytes, as of the last time anything asked.
 *
 * <p>Three states, not two, and the third is the one that matters: a destination nothing has ever probed is
 * NOT the same as one that was checked and works. Rendering the unknown as neutral-green is how a settings
 * page tells a comforting lie, so it is rendered as its own thing and says so.</p>
 */
export function StorageHealthBadge({ health, currentRevision }: { health?: StorageProfileHealthSummary | null; currentRevision: number }) {
  if (!health) return <span className="cn-status" title="No probe has run against this destination yet.">not checked</span>;

  const stale = health.profileRevision < currentRevision;

  // A profile that admits no write refused the probe before anything opened a driver, so nothing here observed the
  // destination. Reads of every object stored under it are still admitted, and may be perfectly fine — wording this
  // as "unreachable" would assert a destination fact no probe measured.
  if (health.failureCode === "ProfileNotActive") {
    return (
      <span className="cn-status cn-status-warn" title={`Checked ${when(health.observedAt)}: this profile's state admits no new writes, so none was attempted. Nothing here says whether the destination itself answers.`}>
        <span className="cn-status-dot" aria-hidden="true" />
        writes disabled
      </span>
    );
  }

  if (health.status !== "Available") {
    return (
      <span className={`cn-status cn-status-error`} title={failureTitle(health)}>
        <span className="cn-status-dot" aria-hidden="true" />
        {label(health)}
      </span>
    );
  }

  // Available, but the probe exercised an older revision — it describes a destination this profile has left.
  if (stale) {
    return (
      <span className="cn-status cn-status-warn" title={`The last successful probe was against revision ${health.profileRevision}; this profile is now on revision ${currentRevision}, which nothing has checked.`}>
        <span className="cn-status-dot" aria-hidden="true" />
        unchecked since it changed
      </span>
    );
  }

  // Available, and a read-only probe. Reachable is a weaker claim than "a run's bytes will land".
  if (!health.writeVerified) {
    return (
      <span className="cn-status cn-status-warn" title={`Reachable as of ${when(health.observedAt)}, but no write was attempted.`}>
        <span className="cn-status-dot" aria-hidden="true" />
        reachable, writes unverified
      </span>
    );
  }

  return (
    <span className="cn-status cn-status-active" title={`A test object was written and removed ${when(health.observedAt)} (${health.latencyMilliseconds} ms).`}>
      <span className="cn-status-dot" aria-hidden="true" />
      writes verified
    </span>
  );
}

/** The operator-facing word for a failing status. Each says what the destination did, not what the code is called. */
function label(health: StorageProfileHealthSummary): string {
  switch (health.status) {
    case "ReadOnly": return "refusing writes";
    case "Degraded": return "answering unreliably";
    case "Unavailable": return "unreachable";
    case "Cancelled": return "check did not finish";
    default: return "not taking bytes";
  }
}

/**
 * The provider's own stage and code, carried verbatim. It is the only thing that tells an operator which end to
 * fix — a credential problem and a bucket problem look identical without it.
 */
function failureTitle(health: StorageProfileHealthSummary): string {
  const reason = health.failureStage && health.failureCode ? ` — ${health.failureStage}/${health.failureCode}` : "";
  return `Last checked ${when(health.observedAt)}${reason}. New writes for any data class routed here will not land.`;
}
