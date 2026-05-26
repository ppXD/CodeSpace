import { Ic } from "./icons";

/**
 * Shared GitHub-style numbered pager. Used by the PR list AND the Add-Repository
 * picker, so kept here as a standalone module rather than nested inside either
 * caller. Visual layout: « Previous   1 … N-1 [N] N+1 … last   Next »
 *
 * When `totalPages` is provided, anchors first/last and elides the middle —
 * matches GitHub's PR list pattern exactly. When it's null (e.g. provider can't
 * give us a cheap total), falls back to an open-ended shape (no last anchor;
 * Next-arrow drives discovery one page at a time).
 */
export function Pager({ current, totalPages, hasNext, loading, onChange }: {
  current: number;
  totalPages: number | null;
  hasNext: boolean;
  loading: boolean;
  onChange: (next: number) => void;
}) {
  const pageNumbers = totalPages != null
    ? computePageNumbersKnown(current, totalPages)
    : computePageNumbersUnknown(current, hasNext);

  return (
    <nav className="pr-pager" aria-label="Pagination">
      <button
        className="pr-pager-step"
        disabled={current === 1 || loading}
        onClick={() => onChange(current - 1)}
        aria-label="Previous page"
      >
        <Ic.ChevronLeft size={12} /> Previous
      </button>

      <div className="pr-pager-nums">
        {pageNumbers.map((p, i) => p === "ellipsis"
          ? <span key={`e${i}`} className="pr-pager-ellipsis">…</span>
          : (
            <button
              key={p}
              className="pr-pager-num"
              data-current={p === current}
              disabled={p === current || loading}
              onClick={() => onChange(p)}
              aria-label={`Page ${p}`}
              aria-current={p === current ? "page" : undefined}
            >
              {p}
            </button>
          ))}
      </div>

      <button
        className="pr-pager-step"
        disabled={!hasNext || loading}
        onClick={() => onChange(current + 1)}
        aria-label="Next page"
      >
        Next <Ic.ChevronRight size={12} />
      </button>
    </nav>
  );
}

/**
 * Total-aware page-number list. Always anchors page 1 and the last page; pads
 * ±1 around the current page; inserts "…" only where there's a real gap.
 *
 * Examples (current, totalPages):
 *   (1, 1)   → [1]
 *   (1, 3)   → [1, 2, 3]
 *   (1, 50)  → [1, 2, …, 50]
 *   (10, 50) → [1, …, 9, 10, 11, …, 50]
 *   (49, 50) → [1, …, 48, 49, 50]
 */
export function computePageNumbersKnown(current: number, totalPages: number): (number | "ellipsis")[] {
  if (totalPages <= 1) return [1];

  const pages: (number | "ellipsis")[] = [1];

  if (current - 1 > 2) pages.push("ellipsis");

  const windowStart = Math.max(2, current - 1);
  const windowEnd = Math.min(totalPages - 1, current + 1);
  for (let i = windowStart; i <= windowEnd; i++) pages.push(i);

  if (current + 1 < totalPages - 1) pages.push("ellipsis");

  pages.push(totalPages);
  return pages;
}

/**
 * Unknown-total fallback — no last-page anchor; Next-arrow drives discovery.
 *
 * Examples (current, hasNext):
 *   (1, false) → [1]
 *   (1, true)  → [1, 2]
 *   (4, true)  → [1, …, 3, 4, 5]
 */
export function computePageNumbersUnknown(current: number, hasNext: boolean): (number | "ellipsis")[] {
  const pages: (number | "ellipsis")[] = [1];

  if (current - 1 > 2) pages.push("ellipsis");

  if (current > 2) pages.push(current - 1);
  if (current > 1) pages.push(current);
  if (hasNext) pages.push(current + 1);

  return pages;
}
