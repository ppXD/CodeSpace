/**
 * Author-time mirror of the backend flow.map resultKey rule (DefinitionValidator + WorkflowOutputKeys.Map +
 * IdentifierPattern). The save-time validator is the authoritative hard gate; this is a hand-maintained copy
 * that gives inline feedback in MapEditor before the operator hits Save. If it ever drifts from the backend
 * the only cost is a missed/early inline hint — the backend still rejects the bad key at save, never a
 * correctness hole. Keep the reserved set + pattern below in step with WorkflowOutputKeys.Map by hand.
 */

/** The keys the map reducer always emits alongside the result array — a resultKey of one would be silently overwritten. */
export const MAP_RESERVED_RESULT_KEYS = ["count", "failed"] as const;

const IDENTIFIER_PATTERN = /^[a-zA-Z_][a-zA-Z0-9_]*$/;

/** The author-time problem with a resultKey, or null when it's valid (a blank key is valid — the engine defaults it to "results"). */
export function resultKeyError(key: string): string | null {
  const trimmed = key.trim();
  if (trimmed === "") return null;
  if ((MAP_RESERVED_RESULT_KEYS as readonly string[]).includes(trimmed))
    return `"${trimmed}" is reserved — the map always emits ${MAP_RESERVED_RESULT_KEYS.join(" / ")}, so it would overwrite the result array. Pick another name.`;
  if (!IDENTIFIER_PATTERN.test(trimmed))
    return `"${trimmed}" can't be referenced — use letters, digits and underscores (starting with a letter or underscore).`;
  return null;
}
