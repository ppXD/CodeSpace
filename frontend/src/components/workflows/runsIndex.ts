/** Common provider tokens → their proper display name; anything else title-cases. Keeps "From GitHub" out of "Github". */
const PROVIDER_NAMES: Record<string, string> = { github: "GitHub", gitlab: "GitLab", bitbucket: "Bitbucket" };

const titleCase = (s: string): string => (s ? s.charAt(0).toUpperCase() + s.slice(1) : s);

/**
 * The run's origin class as a plain-language chip label — a friendlier grain than the Workflow/Task binary, driven by
 * the DB-computed `run_kind` (see backend `RunKinds`). An unknown/future kind title-cases as a safe fallback, so a new
 * engine kind never renders blank.
 */
export function runKindLabel(runKind: string): string {
  switch (runKind) {
    case "workflow": return "Automation";
    case "task": return "Task";
    case "event": return "Triggered";
    case "replay": return "Re-run";
    case "schedule": return "Scheduled";
    case "api": return "API";
    case "child": return "Sub-workflow";
    default: return runKind ? titleCase(runKind) : "Run";
  }
}

/**
 * How a run was launched, in plain language — a provenance sub-line, NEVER a title. Maps the common `source_type`
 * tokens; a provider trigger names the provider ("From GitHub"); anything unrecognised title-cases as a safe fallback
 * so an unknown source is legible rather than raw.
 */
export function sourceLabel(sourceType: string): string {
  if (!sourceType) return "Run";
  if (sourceType === "manual") return "Launched by you";
  if (sourceType.startsWith("schedule")) return "Scheduled";
  if (sourceType === "workflow.child") return "Sub-workflow";
  if (sourceType === "api") return "API";
  if (sourceType === "replay" || sourceType === "rerun") return "Re-run";
  if (sourceType.startsWith("provider.")) {
    const key = sourceType.split(".")[1] ?? "";
    return `From ${PROVIDER_NAMES[key] ?? (key ? titleCase(key) : "a provider")}`;
  }

  return titleCase(sourceType);
}
