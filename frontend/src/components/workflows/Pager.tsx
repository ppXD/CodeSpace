import { Ic } from "@/_imported/ai-code-space/icons";

import { pagerPages } from "./pagerPages";

/**
 * A numbered pager — ◀ 1 2 … N ▶ — over `total` rows in `pageSize` chunks. The current page is accented; the arrows
 * disable at the ends; clicking a number or arrow calls `onPage` with the 1-based target. Renders nothing when there's
 * a single page (or none). Read-only presentation; the page state lives with the caller.
 */
export function Pager({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (page: number) => void }) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  if (totalPages <= 1) return null;

  // A caller's page can fall out of range when the result set shrinks under it — clamp so the accent, the
  // aria-current, and the arrow bounds always point at a real page (the parent reconciles its own state separately).
  const cur = Math.min(Math.max(page, 1), totalPages);

  return (
    <nav className="runs-pager" aria-label="History pages">
      <button type="button" className="runs-pager-arrow" disabled={cur <= 1} onClick={() => onPage(cur - 1)} aria-label="Previous page"><Ic.ChevronLeft size={15} /></button>

      {pagerPages(cur, totalPages).map((it, i) => it === "ellipsis"
        ? <span key={`gap-${i}`} className="runs-pager-gap" aria-hidden="true">…</span>
        : <button key={it} type="button" className="runs-pager-num" data-current={it === cur || undefined} aria-current={it === cur ? "page" : undefined} onClick={() => onPage(it)}>{it}</button>)}

      <button type="button" className="runs-pager-arrow" disabled={cur >= totalPages} onClick={() => onPage(cur + 1)} aria-label="Next page"><Ic.ChevronRight size={15} /></button>
    </nav>
  );
}
