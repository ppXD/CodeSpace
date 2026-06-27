import type { LaunchTaskInput, TaskSurfaceKind } from "@/api/tasks";

/** One repo in the launch workspace. `isPrimary` marks the repo whose id+branch become the run's
 *  primary `repositoryId` / `baseBranch`; every other repo becomes a related-repository entry. */
export interface LaunchWorkspaceRepo {
  repositoryId: string;
  branch: string;
  access: "write" | "read";
  alias: string;
  isPrimary: boolean;
}

/** The slice of Launch-modal state that maps to backend params. Pure data — the modal owns the React
 *  state; this is the snapshot handed to {@link buildLaunchInput} so the mapping is unit-testable in
 *  isolation (no DOM). Caps fields are strings because they come from text inputs / a select. */
export interface LaunchFormState {
  taskText: string;
  surface: TaskSurfaceKind;
  workspace: LaunchWorkspaceRepo[];
  effort: string;
  autonomy: string;
  model: string;
  modelCredentialId: string;
  /** The picked model's ROW id (`ModelCredentialModel` id), resolved from `(model, modelCredentialId)`. On Deep it
   *  pins the supervisor brain; on single-agent the agent model. Empty ⇒ Auto. */
  modelCredentialModelId: string;
  harness: string;
  agentDefinitionId: string;
  runnerKind: string;
  /** Coordination "Limits" — supervisor fan-out bounds. Only meaningful on deep/auto (see effort gate). */
  maxParallel: string;
  maxRounds: string;
  maxAgents: string;
  /** Coordination "Budget" — `"none"` or a dollar amount string (`"5"`/`"10"`/`"25"`). */
  budget: string;
  /** Coordination "Agent model pool" — credentialed-model ROW ids the dispatched agents may use. Empty = all. */
  agentModels: string[];
  /** Coordination "Autonomy ceiling" — a tier name, or `""` (Inherit the preset). Tighten-only on the backend. */
  autonomyCeiling: string;
  /** Coordination "Integrate branches" — Deep only: opt in to integrating the spawned agents' diffs into one reviewable branch at merge. Default false ⇒ defer to the ambient flag. */
  integrateBranches: boolean;
  /** Coordination "Acceptance" — Deep only: free-text criteria the supervisor targets (rendered into its prompt). Sent only when changed from {@link DEFAULT_ACCEPTANCE} (unmodified ⇒ omitted, byte-identical). */
  acceptanceCriteria: string[];
  /** "Time limit" — the per-agent wall-clock as a seconds string: `"3600"` (1h, the default), `"0"` (No limit / unbounded), etc. Applies to ALL tiers (a per-agent execution setting, unlike the deep/auto-gated Coordination caps). */
  timeLimit: string;
}

/** The canonical default acceptance chips — shared by the modal seed/reset and the omit-check, so an UNMODIFIED set is
 *  recognised and omitted (byte-identical). The operator activates criteria by changing this set (deleting / editing). */
export const DEFAULT_ACCEPTANCE = ["tests pass", "PR opened"];

const primaryOf = (workspace: LaunchWorkspaceRepo[]) => workspace.find(r => r.isPrimary) ?? workspace[0];

/** Parse a positive-int cap from a text field. Blank / non-numeric / `< 1` ⇒ undefined (omit the cap, so
 *  the backend keeps the effort preset's default — the launch stays byte-identical to an unset field). */
const posIntCap = (raw: string): number | undefined => {
  const n = Number.parseInt(raw, 10);
  return Number.isFinite(n) && n >= 1 ? n : undefined;
};

/** True when the launch tier exposes the Coordination caps (Limits / Budget). Those bound a fan-out /
 *  supervisor loop, so they only apply — and are only VISIBLE in the modal — on deep/auto. Sending them
 *  on quick/standard would impose a cap the operator never saw (the Coordination tab is hidden there). */
const tierExposesCaps = (effort: string) => effort === "deep" || effort === "auto";

/**
 * Map the Launch-modal form state to the wire `LaunchTaskInput`. The single source of truth for what the
 * modal sends — extracted as a pure function so every field, the multi-repo split, and the caps gating are
 * exhaustively unit-tested. Optional fields are OMITTED (undefined) when the operator leaves a default, so
 * an unconfigured launch is byte-identical to the minimal command the backend's projection fills in.
 */
export function buildLaunchInput(state: LaunchFormState): LaunchTaskInput {
  const primary = primaryOf(state.workspace);

  const input: LaunchTaskInput = {
    taskText: state.taskText.trim(),
    surfaceKind: state.surface,
    repositoryId: primary?.repositoryId || null,
    baseBranch: primary?.branch || null,
    effort: state.effort,
    autonomy: state.autonomy,
    model: state.model || null,
    harness: state.harness || null,
    agentDefinitionId: state.agentDefinitionId || null,
    runnerKind: state.runnerKind || null,
    modelCredentialId: state.modelCredentialId || null,
    modelCredentialModelId: state.modelCredentialModelId || null,
  };

  const relatedRepositories = buildRelatedRepositories(state.workspace, primary);
  if (relatedRepositories) input.relatedRepositories = relatedRepositories;

  // The per-agent wall-clock — sent on ALL tiers (a per-agent setting, unlike the deep/auto-gated caps). The default
  // "3600" (1h) is OMITTED so an untouched launch stays byte-identical to the backend default; "0" = No limit
  // (unbounded — the backend maps 0 → no wall-clock) is sent explicitly, as is any other non-default value.
  const timeLimit = Number.parseInt(state.timeLimit, 10);
  if (Number.isFinite(timeLimit) && timeLimit >= 0 && timeLimit !== 3600) input.timeoutSeconds = timeLimit;

  const caps = tierExposesCaps(state.effort) ? buildCaps(state) : undefined;
  if (caps) input.caps = caps;

  // The agent model pool is a supervisor-lane bound (inert on a single-agent run), and the Coordination tab that
  // sets it is only shown on deep/auto — so gate it the same way as caps. Empty ⇒ omit (all the team's models).
  if (tierExposesCaps(state.effort) && state.agentModels.length) input.allowedModelIds = [...state.agentModels];

  // The autonomy ceiling is a Coordination knob (deep/auto only); "" means Inherit the preset ⇒ omit the key.
  if (tierExposesCaps(state.effort) && state.autonomyCeiling) input.autonomyCeiling = state.autonomyCeiling;

  // Integrate-branches is a Deep-only supervisor opt-in; send it only when ON (default off defers to the ambient flag,
  // byte-identical) and only on the tiers that expose Coordination (inert on a single-agent run).
  if (tierExposesCaps(state.effort) && state.integrateBranches) input.integrateBranches = true;

  // Acceptance criteria are a Deep-only supervisor concern. Send only when the operator CHANGED them from the canonical
  // default AND the set is non-empty — an unmodified default (or a cleared set) is omitted, keeping the supervisor
  // prompt byte-identical. (The modal default is non-empty, so omit-when-empty alone would always send it.)
  if (tierExposesCaps(state.effort) && state.acceptanceCriteria.length
      && JSON.stringify(state.acceptanceCriteria) !== JSON.stringify(DEFAULT_ACCEPTANCE)) {
    input.acceptanceCriteria = [...state.acceptanceCriteria];
  }

  return input;
}

/** Every workspace repo EXCEPT the primary becomes a related-repository. Blank alias ⇒ omitted (the
 *  backend derives one). Empty ⇒ undefined so the key is omitted (single-repo launch is unchanged). */
function buildRelatedRepositories(workspace: LaunchWorkspaceRepo[], primary: LaunchWorkspaceRepo | undefined) {
  const related = workspace
    .filter(r => r !== primary && r.repositoryId)
    .map(r => {
      const alias = r.alias.trim();
      return { repositoryId: r.repositoryId, access: r.access, ...(alias ? { alias } : {}) };
    });

  return related.length ? related : undefined;
}

/** The Coordination "Limits" + "Budget" as the backend `caps` (TaskCapsOverride). Each cap is included
 *  only when set to a real value; budget `"none"` ⇒ no cost cap. All-unset ⇒ undefined (omit the key). */
function buildCaps(state: LaunchFormState) {
  const caps: NonNullable<LaunchTaskInput["caps"]> = {};

  const maxParallelism = posIntCap(state.maxParallel);
  if (maxParallelism !== undefined) caps.maxParallelism = maxParallelism;

  const maxRounds = posIntCap(state.maxRounds);
  if (maxRounds !== undefined) caps.maxRounds = maxRounds;

  const maxTotalSpawns = posIntCap(state.maxAgents);
  if (maxTotalSpawns !== undefined) caps.maxTotalSpawns = maxTotalSpawns;

  if (state.budget !== "none") {
    const cost = Number(state.budget);
    if (Number.isFinite(cost) && cost > 0) caps.maxCostUsd = cost;
  }

  return Object.keys(caps).length ? caps : undefined;
}
