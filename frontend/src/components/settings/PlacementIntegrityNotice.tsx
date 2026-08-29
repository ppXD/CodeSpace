import type { PlacementIntegritySummary } from "@/api/storage";

/**
 * What became of the bytes this team already stored.
 *
 * <p>The storage page otherwise only ever describes what happens NEXT — which destination the next write lands on,
 * and whether that destination is reachable. Neither notices that an object written a year ago is gone, because
 * probing a healthy bucket says nothing about what it no longer contains. Until a loss is stated here, it surfaces
 * only when a person opens an artifact and gets an error.</p>
 */
export function PlacementIntegrityNotice({ integrity }: { integrity?: PlacementIntegritySummary | null }) {
  if (!integrity) return null;

  const lost = integrity.missing + integrity.corrupt;
  const stored = integrity.available + lost;

  if (stored === 0) return null;

  if (lost > 0) {
    return (
      <div className="cn-banner-p" role="status">
        <span className="cn-status cn-status-error">
          <span className="cn-status-dot" aria-hidden="true" />
          {describeLoss(integrity)}
        </span>{" "}
        out of {stored.toLocaleString()} stored. Opening one returns an error rather than the wrong bytes; the
        placement record keeps which destination held it.
      </div>
    );
  }

  return (
    <div className="cn-banner-p" role="status">
      <span className="cn-status cn-status-active">
        <span className="cn-status-dot" aria-hidden="true" />
        {stored.toLocaleString()} stored, all present
      </span>{" "}
      {lastConfirmed(integrity.oldestVerifiedAt)}
    </div>
  );
}

/** Names the two losses apart, because they are different problems: one is gone, the other is not what was recorded. */
function describeLoss(integrity: PlacementIntegritySummary): string {
  const parts: string[] = [];
  if (integrity.missing > 0) parts.push(`${integrity.missing.toLocaleString()} no longer at their destination`);
  if (integrity.corrupt > 0) parts.push(`${integrity.corrupt.toLocaleString()} replaced by something else`);
  return parts.join(", ");
}

/**
 * How far back a loss could have gone unnoticed — the WORST-checked placement, not the average.
 *
 * <p>Written as the age of the oldest confirmation rather than "last checked", because every placement carries a
 * confirmation from the moment its bytes were written; the number is the outer edge of what is currently known.</p>
 */
function lastConfirmed(oldestVerifiedAt: string | null): string {
  if (!oldestVerifiedAt) return "";

  const days = Math.floor((Date.now() - new Date(oldestVerifiedAt).getTime()) / 86_400_000);
  if (days < 1) return "every one confirmed within the last day.";
  if (days === 1) return "the least recently confirmed was checked a day ago.";
  return `the least recently confirmed was checked ${days} days ago.`;
}
