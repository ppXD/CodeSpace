import type { LaunchTaskInput, TaskSurfaceKind } from "@/api/tasks";
import type { QualityTier } from "./qualityPresets";

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
  /** When set, the launch CONTINUES this work session as its next turn (binds to `LaunchTaskCommand.SessionId`). The
   *  session-room composer passes it; the Launch modal leaves it unset (a fresh session is opened). */
  sessionId?: string;
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
  /** "Working dir" — multi-repo cwd mode: `"auto"` (default) / `"workspace"` / `"primary"`. Sent (as `workingDirMode`) only when non-auto. Applies to all tiers (an agent-setup knob); inert on a single-repo run. */
  cwdMode: string;
  /** "Force MCP fabric" — per-run opt-in to the FULL (side-effecting) MCP tool catalog. Default false ⇒ omitted ⇒ defer to the ambient flag (byte-identical). Sent (as `enableMcp:true`) only when on. Applies to all tiers (an agent-setup knob). */
  enableMcp: boolean;
  /** "Tools" — a Claude-only tool allow-list (canonical names). Empty ⇒ omitted ⇒ harness default (all tools), byte-identical. Non-empty ⇒ sent as `allowedTools`. Additive against a persona's tools; not a write boundary. */
  tools: string[];
  /** "Publish branch" — per-run opt-in to publishing the agent's diff as a branch even when the ambient push flag is off. Default false ⇒ omitted ⇒ defer to the ambient flag (byte-identical). Sent (as `pushBranch:true`) only when on. All tiers. */
  pushBranch: boolean;
  /** Coordination "Limits" — the max agents that run CONCURRENTLY (the only agent knob; a supervised run loops until
   *  done, bounded by the cost budget + no-progress, not a round/total-agent count). Only meaningful on deep/auto. */
  maxParallel: string;
  /** Coordination "Budget" — `"none"` or a dollar amount string (`"5"`/`"10"`/`"25"`). The realized-spend cap that bounds a loop-until-done run. */
  budget: string;
  /** Coordination "Agent model pool" — credentialed-model ROW ids the dispatched agents may use. Empty = all. */
  agentModels: string[];
  /** Coordination "Agent pool" — AgentDefinition (persona) ROW ids the supervisor may dispatch. Empty = all the team's personas. */
  agentPool: string[];
  /** Coordination "Autonomy ceiling" — a tier name, or `""` (Inherit the preset). Tighten-only on the backend. */
  autonomyCeiling: string;
  /** Coordination "Integrate branches" — Deep only: opt in to integrating the spawned agents' diffs into one reviewable branch at merge. Default false ⇒ defer to the ambient flag. */
  integrateBranches: boolean;
  /** Evaluation "Acceptance criteria" — every tier (S5b): Deep renders them into the supervisor prompt, Standard into the planner prompt (per-item contracts target them), Quick into the agent's goal. Sent only when changed from {@link DEFAULT_ACCEPTANCE} (unmodified ⇒ omitted, byte-identical). */
  acceptanceCriteria: string[];
  /** Evaluation "Acceptance checks" — the EXECUTABLE argv floor (one element per token, e.g. ["sh","check.sh"]): Deep runs it at the terminal stop; Quick grades the single agent's produced branch (S5). Standard verifies per item via the plan's own contracts, so it never sends this. Sent only when non-empty (⇒ omitted, byte-identical). */
  acceptanceChecks: string[];
  /** Planning "Confirm plan first" — any planning tier (standard/auto/deep): park each authored plan version for the operator's confirmation before any agent runs. Sent only when ON (default off ⇒ omitted, byte-identical); quick authors no plan, so it never sends. */
  requirePlanConfirmation: boolean;
  /** Planning "Plan critic" — every planning tier (standard/auto/deep): the plan.author/plan.confirm reviewMode on the plan-map tiers, the supervisor's plan-scoped planReviewMode on Deep. Sent (as the enum name) only when not None; quick authors no plan. */
  plannerReview: string;
  /** "Time limit" — the per-agent wall-clock as a seconds string: `"3600"` (1h, the default), `"0"` (No limit / unbounded), etc. Applies to ALL tiers (a per-agent execution setting, unlike the deep/auto-gated Coordination caps). */
  timeLimit: string;
  /** Review "Decisions" — how an independent critic reviews each supervisor DECISION: `"None"` (default) / `"Gate"` / `"Improve"`. Deep only. Sent (as the enum name) only when not None. */
  decisionReview: string;
  /** Review "Agent output" — how an independent critic reviews each agent's produced change: `"None"` (default) / `"Gate"` / `"Improve"` (feed the critique back for a bounded self-revision). All tiers. Sent only when not None. */
  outputReview: string;
  /** Review "Reviewer model" — the credentialed-model ROW id the critic(s) run on, or `""` (Auto). Sent only when a review mode is active. */
  reviewerModel: string;
  /** Evaluation "Self-revise" (S6) — the bounded in-run revise budget when a check fails / the Improve critic flags: `""` (Auto — the backend default: 1 under Improve, else 0) / `"0"` (Off, kills even Improve's implied round) / `"1"` / `"2"`. Quick/standard/auto (deep units revise via the supervisor's own retry). Sent only when not Auto. */
  reviseRounds: string;
  /** Evaluation "Reviewer" (S8) — run the output review as a REAL independent agent (read-only clone of the produced branch, distinct-harness-first, model-critic fallback): `false` (default, the in-process model critic) / `true`. Sent only when true AND a review mode is active. */
  reviewerAgent: boolean;
  /** P3.2: the QUALITY tier the operator explicitly picked (the Quality preset bar) — tracked independently of the
   *  knob values below, NOT re-derived from them: hand-editing a knob after picking Delivery must NOT quietly drop
   *  the tier back to Prototype (the mandate is a FLOOR the operator declared, not an inference from the current
   *  mix). Sent only when not Prototype (⇒ omitted, byte-identical to before this field existed). */
  tier: QualityTier;
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

  // Continue an existing session as its next turn (the session-room composer sets it); unset ⇒ a fresh session opens.
  if (state.sessionId) input.sessionId = state.sessionId;

  const relatedRepositories = buildRelatedRepositories(state.workspace, primary);
  if (relatedRepositories) input.relatedRepositories = relatedRepositories;

  // Working-dir mode is an agent-setup knob (all tiers), inert on a single-repo run. "auto" is the default ⇒ omitted ⇒
  // byte-identical; "workspace"/"primary" are sent so a multi-repo run anchors the cwd where the operator asked.
  if (state.cwdMode && state.cwdMode !== "auto") input.workingDirMode = state.cwdMode;

  // Force-MCP is a default-OFF per-run opt-in (the OR-gate can force the full fabric ON, never OFF). Send true only when
  // on (default off ⇒ omitted ⇒ defer to the ambient flag, byte-identical). An agent-setup knob ⇒ all tiers.
  if (state.enableMcp) input.enableMcp = true;

  // The tool allow-list (a Claude-only capability filter). Empty ⇒ omitted ⇒ the harness default (all tools) ⇒
  // byte-identical; a non-empty pick is sent verbatim. An agent-setup knob ⇒ all tiers.
  if (state.tools.length) input.allowedTools = [...state.tools];

  // Publish-branch is a default-OFF per-run opt-in (the OR-gate forces publish ON, never OFF). Send true only when on
  // (default off ⇒ omitted ⇒ defer to the ambient flag, byte-identical). An agent-setup knob ⇒ all tiers.
  if (state.pushBranch) input.pushBranch = true;

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

  // The agent (persona) pool — same deep/auto gating as the model pool; empty ⇒ omit (all the team's personas).
  if (tierExposesCaps(state.effort) && state.agentPool.length) input.allowedAgentDefinitionIds = [...state.agentPool];

  // The autonomy ceiling is a Coordination knob (deep/auto only); "" means Inherit the preset ⇒ omit the key.
  if (tierExposesCaps(state.effort) && state.autonomyCeiling) input.autonomyCeiling = state.autonomyCeiling;

  // Integrate-branches is a Deep-only supervisor opt-in; send it only when ON (default off defers to the ambient flag,
  // byte-identical) and only on the tiers that expose Coordination (inert on a single-agent run).
  if (tierExposesCaps(state.effort) && state.integrateBranches) input.integrateBranches = true;

  // Acceptance criteria STEER on every tier (S5b: deep → the supervisor prompt, standard → the planner prompt so
  // per-item contracts target them, quick → the agent's goal). Send only when the operator CHANGED them from the
  // canonical default AND the set is non-empty — an unmodified default (or a cleared set) is omitted, byte-identical.
  if (state.acceptanceCriteria.length
      && JSON.stringify(state.acceptanceCriteria) !== JSON.stringify(DEFAULT_ACCEPTANCE)) {
    input.acceptanceCriteria = [...state.acceptanceCriteria];
  }

  // The plan-confirmation gate + the executable acceptance floor are Deep-only supervisor opt-ins; the plan critic
  // rides the plan-map planner (standard/auto). Defaults ⇒ omitted ⇒ byte-identical.
  if (state.effort !== "quick" && state.requirePlanConfirmation) input.requirePlanConfirmation = true;
  if (state.effort !== "standard" && state.acceptanceChecks.length) input.acceptanceChecks = [...state.acceptanceChecks];

  const plannerOn = state.effort !== "quick" && state.plannerReview !== "None";
  if (plannerOn) input.plannerReviewMode = state.plannerReview;

  // The critic review modes (the enum NAME — the API has a string-enum converter). Decision review is a supervisor
  // concern (deep/auto only); output review applies to any agent run. "None" (the default) ⇒ omitted ⇒ byte-identical.
  // The reviewer model rides along only when a review is actually active (else baking it would not be byte-identical).
  const decisionOn = tierExposesCaps(state.effort) && state.decisionReview !== "None";
  const outputOn = state.outputReview !== "None";
  if (decisionOn) input.decisionReviewMode = state.decisionReview;
  if (outputOn) input.outputReviewMode = state.outputReview;
  if (state.reviewerModel && (decisionOn || outputOn || plannerOn)) input.reviewerModelId = state.reviewerModel;

  // The S8 agent-reviewer opt-in rides only when an output review is actually active (inert otherwise — byte-identical).
  if (outputOn && state.reviewerAgent) input.reviewerAgent = true;

  // The S6 self-revise budget — an explicit round count (incl. "0" = Off, which kills even Improve's implied round)
  // is sent verbatim; "" (Auto) is omitted so the backend default applies (1 under Improve, else 0). Deep is excluded:
  // supervisor units revise through the supervisor's own retry loop, and sending a knob the tab hid would be a lie.
  if (state.effort !== "deep" && state.reviseRounds !== "") {
    const rounds = Number.parseInt(state.reviseRounds, 10);
    if (Number.isFinite(rounds) && rounds >= 0) input.reviseRounds = rounds;
  }

  // P3.2: the quality-tier mandate. Prototype is the backend default ⇒ omitted, byte-identical.
  if (state.tier !== "Prototype") input.tier = state.tier;

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

  // Rounds + total-spawn are NOT operator knobs — a supervised run loops until done, bounded by cost + no-progress +
  // the model's stop (the round/total ceilings survive only as hidden backend back-stops). So the launch never sends them.

  if (state.budget !== "none") {
    const cost = Number(state.budget);
    if (Number.isFinite(cost) && cost > 0) caps.maxCostUsd = cost;
  }

  return Object.keys(caps).length ? caps : undefined;
}
