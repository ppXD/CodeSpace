/**
 * A short, single-line run/session title for lists and breadcrumbs. A task session's title is often the full task
 * prompt (many lines), so lists and crumbs read like a wall of text. This collapses whitespace and caps the length on a
 * word boundary — the row reads like a Claude/Codex summary, and the full text stays available via a `title=` tooltip.
 */
export function shortRunTitle(title: string, max = 64): string {
  const flat = title.replace(/\s+/g, " ").trim();
  if (flat.length <= max) return flat;
  const cut = flat.slice(0, max);
  const sp = cut.lastIndexOf(" ");
  return (sp > max * 0.6 ? cut.slice(0, sp) : cut).replace(/[\s,.;:—-]+$/, "") + "…";
}
