import type { AgentDefinitionSummary } from "@/api/agents";
import type { Option } from "@/components/common/Combo";

/**
 * Pure form model for the agent editor — the editable FormState, its defaults, and the persona→form mapping.
 * Kept out of the component file so the drawer can seed an empty/edit form without that file exporting
 * non-components (the react-refresh rule). No React.
 */

/** The autonomy tiers the run path recognizes (AgentAutonomyLevel, parsed case-insensitively). Stored by name. */
export const AUTONOMY: Option[] = [
  { value: "Confined", label: "Confined", desc: "Analysis only — no writes, no network." },
  { value: "Standard", label: "Standard", desc: "Writes inside its workspace, no network. The safe default." },
  { value: "Trusted", label: "Trusted", desc: "Workspace write + network — for runs that fetch dependencies." },
  { value: "Unleashed", label: "Unleashed", desc: "Highest capability — admin / controlled runners only." },
];
export const DEFAULT_AUTONOMY = "Standard";

export const TOOLS_MODES: Option[] = [
  { value: "inherit", label: "Inherit the harness default" },
  { value: "custom", label: "Custom allow-list" },
];

export interface FormState {
  name: string;
  description: string;
  systemPrompt: string;
  model: string;
  autonomy: string;
  toolsMode: "inherit" | "custom";
  toolsText: string;
}

export const EMPTY_FORM: FormState = { name: "", description: "", systemPrompt: "", model: "", autonomy: DEFAULT_AUTONOMY, toolsMode: "inherit", toolsText: "" };

export function normalizeAutonomy(stored: string | null | undefined): string {
  const match = AUTONOMY.find((t) => t.value.toLowerCase() === (stored ?? "").toLowerCase());
  return match ? match.value : DEFAULT_AUTONOMY;
}

export function parseTools(text: string): string[] {
  return text.split(",").map((t) => t.trim()).filter((t) => t.length > 0);
}

export function formFromPersona(a: AgentDefinitionSummary): FormState {
  return {
    name: a.name,
    description: a.description ?? "",
    systemPrompt: a.systemPrompt ?? "",
    model: a.model ?? "",
    autonomy: normalizeAutonomy(a.defaultAutonomy),
    toolsMode: a.tools === null ? "inherit" : "custom",
    toolsText: a.tools ? a.tools.join(", ") : "",
  };
}
