/**
 * Splits a message body into ordered segments — plain text interleaved with
 * `<reftype:refid|label>` reference tokens — so the renderer can draw inline `@`-chips
 * without losing the surrounding prose.
 *
 * The token grammar MUST match the backend `MessageReferenceParser` exactly (reftype is a
 * lowercase identifier; refid is any run without `|`/`>` so it carries code-location colons
 * and PR `#`; optional `|label`). Unlike the backend — which dedupes for the reverse index —
 * this keeps EVERY occurrence in source order, because each mention renders as its own chip.
 */

export type MessageSegment =
  | { kind: "text"; text: string }
  | { kind: "ref"; refType: string; refId: string; label: string | null };

// Non-global source; we construct a fresh global regex per call so matchAll's lastIndex
// state can't leak across calls (a shared /g regex is stateful and bites on reentry).
const TOKEN_SOURCE = "<([a-z][a-z0-9_]*):([^|>]+)(?:\\|([^>]*))?>";

export function parseMessageBody(body: string): MessageSegment[] {
  if (!body) return [];

  const token = new RegExp(TOKEN_SOURCE, "g");
  const segments: MessageSegment[] = [];
  let lastIndex = 0;

  for (const match of body.matchAll(token)) {
    const start = match.index ?? 0;

    if (start > lastIndex) segments.push({ kind: "text", text: body.slice(lastIndex, start) });

    const rawLabel = match[3];
    const label = rawLabel != null && rawLabel.length > 0 ? rawLabel : null;
    segments.push({ kind: "ref", refType: match[1], refId: match[2], label });

    lastIndex = start + match[0].length;
  }

  if (lastIndex < body.length) segments.push({ kind: "text", text: body.slice(lastIndex) });

  return segments;
}
