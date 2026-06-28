/**
 * Pure display helpers for an agent's runtime (autonomy + tools), shared by the bench card and the detail
 * drawer so both read the AgentDefinition contract identically. No React.
 */

/**
 * One-line permissions summary for an agent's default autonomy tier (AgentAutonomyLevel, matched
 * case-insensitively; unknown/blank → the Standard default). Mirrors the editor's autonomy-picker descriptions.
 */
export function autonomySummary(level: string | null | undefined): string {
  switch ((level ?? "").toLowerCase()) {
    case "confined": return "Analysis only — no writes, no network.";
    case "trusted": return "Writes in its workspace and reaches the network — for runs that fetch dependencies.";
    case "unleashed": return "Highest capability — runs commands, edits files and opens PRs with no approval gates.";
    default: return "Writes inside its workspace, no network. The safe default.";
  }
}

/** Tools tri-state matching the AgentDefinition null-vs-empty contract: null = harness default, [] = none, list = a count. */
export function toolsLabel(tools: string[] | null): string {
  if (tools === null) return "Default tools";
  if (tools.length === 0) return "No tools";

  return `${tools.length} ${tools.length === 1 ? "tool" : "tools"}`;
}
