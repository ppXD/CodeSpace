/** A page number, or the "ellipsis" marker that bridges a gap between page ranges. */
export type PagerItem = number | "ellipsis";

/**
 * The windowed page list for a numbered pager: always the first + last page, a window around the current page, and an
 * "ellipsis" marker bridging any gap — e.g. current=5, total=12 → [1, …, 4, 5, 6, …, 12]. Small totals (≤7) list every
 * page with no ellipsis. `current` is clamped into [1, total]; total ≤ 1 yields just [1]. Pure — unit-testable.
 */
export function pagerPages(current: number, total: number, window = 1): PagerItem[] {
  if (total <= 1) return [1];
  if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1);

  const cur = Math.min(Math.max(current, 1), total);
  const pages = new Set<number>([1, total]);
  for (let p = cur - window; p <= cur + window; p++) if (p >= 1 && p <= total) pages.add(p);

  const sorted = [...pages].sort((a, b) => a - b);
  const out: PagerItem[] = [];
  for (let i = 0; i < sorted.length; i++) {
    if (i > 0 && sorted[i] - sorted[i - 1] > 1) out.push("ellipsis");
    out.push(sorted[i]);
  }

  return out;
}
