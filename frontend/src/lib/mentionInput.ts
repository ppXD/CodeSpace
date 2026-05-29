import type { TeamMemberSummary } from "@/api/teams";

/**
 * Pure helpers for the mention composer. The composer is a contenteditable surface where a mention
 * is an inline, non-editable chip element carrying the structured reference in data-* attributes —
 * NOT visible text. That keeps the reference unambiguous (the id travels with the chip, immune to
 * duplicate names or hand-editing) and lets one serializer cover the whole generic
 * `<reftype:refid|label>` grammar the backend already speaks, so future #channel / PR / code chips
 * drop in without touching this. These functions are DOM-shape-only and caret-free, so they unit
 * test without a live selection.
 */

export const MENTION_TYPE_ATTR = "data-ref-type";
export const MENTION_ID_ATTR = "data-ref-id";
export const MENTION_LABEL_ATTR = "data-label";

/** The attribute bag for a chip of the given reference — the component spreads this onto the span,
 *  and {@link serializeEditor} reads it back out, so the two never drift. */
export function mentionAttributes(refType: string, refId: string, label: string): Record<string, string> {
  return { [MENTION_TYPE_ATTR]: refType, [MENTION_ID_ATTR]: refId, [MENTION_LABEL_ATTR]: label };
}

/**
 * Serialize a contenteditable subtree to a message body: text nodes verbatim, a `<br>` to a
 * newline, and any element carrying {@link MENTION_TYPE_ATTR} to its `<reftype:refid|label>` token.
 * Other elements (e.g. a browser-wrapped line div) are recursed into so stray wrappers don't drop
 * content. Generic: the token is built from the chip's attributes, not from a per-type branch.
 */
export function serializeEditor(root: Node): string {
  let out = "";

  root.childNodes.forEach((node) => {
    if (node.nodeType === Node.TEXT_NODE) {
      out += node.textContent ?? "";
      return;
    }

    if (!(node instanceof HTMLElement)) return;

    const refType = node.getAttribute(MENTION_TYPE_ATTR);
    if (refType) {
      const refId = node.getAttribute(MENTION_ID_ATTR) ?? "";
      const label = node.getAttribute(MENTION_LABEL_ATTR) ?? "";
      out += `<${refType}:${refId}|${label}>`;
      return;
    }

    if (node.tagName === "BR") {
      out += "\n";
      return;
    }

    out += serializeEditor(node);
  });

  return out;
}

/**
 * Given the text immediately before the caret, the in-progress `@`-mention query, or null when the
 * caret isn't in one. The `@` must start the text or follow whitespace (so an email's `@` never
 * triggers it), and the query is an unbroken run of name characters — a space closes the mention.
 */
export function findActiveMention(textBeforeCaret: string): { query: string } | null {
  const match = /(?:^|\s)@([\p{L}\p{N}_-]*)$/u.exec(textBeforeCaret);
  return match ? { query: match[1] } : null;
}

/** Members whose name (or email local-part) matches the query, name-prefix matches first, capped.
 *  An empty query lists everyone (up to the cap) so a bare `@` still offers the roster. */
export function matchMembers(members: readonly TeamMemberSummary[], query: string, limit = 8): TeamMemberSummary[] {
  const q = query.trim().toLowerCase();

  const ranked = members
    .map((m) => ({ m, score: score(m, q) }))
    .filter((x) => x.score >= 0)
    .sort((a, b) => a.score - b.score || a.m.name.localeCompare(b.m.name));

  return ranked.slice(0, limit).map((x) => x.m);
}

// Lower is better: 0 = name prefix, 1 = email prefix, 2 = name/email substring, -1 = no match.
function score(member: TeamMemberSummary, q: string): number {
  if (q.length === 0) return 0;

  const name = member.name.toLowerCase();
  const email = member.email.toLowerCase();

  if (name.startsWith(q)) return 0;
  if (email.startsWith(q)) return 1;
  if (name.includes(q) || email.includes(q)) return 2;
  return -1;
}
