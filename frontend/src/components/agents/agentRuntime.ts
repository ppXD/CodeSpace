/**
 * Pure display helper for an agent's tools, used by the bench card so it reads the AgentDefinition contract
 * exactly. No React.
 */

/** Tools tri-state matching the AgentDefinition null-vs-empty contract: null = harness default, [] = none, list = a count. */
export function toolsLabel(tools: string[] | null): string {
  if (tools === null) return "Default tools";
  if (tools.length === 0) return "No tools";

  return `${tools.length} ${tools.length === 1 ? "tool" : "tools"}`;
}
