/** Longest auto-derived agent name. Past this we cut at the last word boundary so the
 * default name stays a clean phrase (the user can always rename it in the editor). */
const MAX_NAME_LENGTH = 60;

/** Fallback when the task text has no usable content (empty / whitespace only). */
export const DEFAULT_AGENT_NAME = "New agent";

/**
 * Derive a friendly default agent name from a free-text task description (the "Describe a task"
 * create on-ramp). Takes the first non-empty line, collapses runs of whitespace, and caps the
 * length at a word boundary — never mid-word, never with an ellipsis (it's an editable name, not
 * a label). Empty / whitespace-only input falls back to {@link DEFAULT_AGENT_NAME}.
 */
export function deriveAgentName(task: string): string {
  const firstLine = (task.split("\n").find((l) => l.trim() !== "") ?? "").replace(/\s+/g, " ").trim();

  if (firstLine === "") return DEFAULT_AGENT_NAME;

  if (firstLine.length <= MAX_NAME_LENGTH) return firstLine;

  const capped = firstLine.slice(0, MAX_NAME_LENGTH);
  const lastSpace = capped.lastIndexOf(" ");

  // Only honour the word boundary if it leaves a reasonable chunk; otherwise hard-cap (e.g. one
  // very long token) so we never return a near-empty name.
  return (lastSpace > 20 ? capped.slice(0, lastSpace) : capped).trim();
}
