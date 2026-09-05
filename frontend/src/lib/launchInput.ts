import type { LaunchTaskInput, RoutePreviewInput, TaskSurfaceKind } from "@/api/tasks";
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

/** A nullable backend boolean rendered truthfully: inherit omits the field; on/off send an explicit value. */
export type LaunchBooleanOverride = "inherit" | "on" | "off";

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
  /** The deliverable shape a prior route preview classified for THIS task text, kept when the operator answers the
   *  confirm card with a tier. Sent as `deliverableShape` so the explicit-tier path (which skips the classifier)
   *  keeps the shape instead of reverting to `code`. Undefined ⇒ omitted ⇒ the backend's own reading. */
  deliverableShape?: string;
  /** The requested permission tier — ALSO the network choice, since network is not an independent axis: `Trusted`
   *  is the one tier `AgentAutonomyPolicy.Derive` grants `Network.On`. One field, so the composer's Permissions row
   *  and its Network row can never disagree about what the launch asks for. See {@link effectiveAutonomy}. */
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
  /** "MCP fabric" — inherit the ambient/profile choice, or explicitly narrow/widen this run. */
  enableMcp: LaunchBooleanOverride;
  /** "Tools" — a Claude-only tool allow-list (canonical names). Empty ⇒ omitted ⇒ harness default (all tools), byte-identical. Non-empty ⇒ sent as `allowedTools`. Additive against a persona's tools; not a write boundary. */
  tools: string[];
  /** "Publish branch" — inherit the ambient/profile choice, or explicitly narrow/widen this run. */
  pushBranch: LaunchBooleanOverride;
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
  /** Coordination "Integrate branches" — Deep only; inherit, explicitly on, or explicitly off. */
  integrateBranches: LaunchBooleanOverride;
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
 * Which tiers may send a BUDGET — a strictly wider set than {@link tierExposesCaps}, and deliberately its own
 * predicate. Standard joined once the engine began admitting each map branch against the run's budget ledger;
 * before that a budget it sent would have been silently ignored, which is worse than not offering one. Widening
 * `tierExposesCaps` itself would have been the obvious edit and the wrong one: it also gates the agent-model pool,
 * the persona pool, the autonomy ceiling and the parallelism override, all supervisor-lane concepts — a standard
 * run would have started overriding the Standard preset's own concurrency with the form's default.
 *
 * Quick stays out of both: a single agent is already running by the time it spends, so there is no admission
 * point to refuse at, and a cap there would be a promise the engine cannot keep.
 */
const tierExposesBudget = (effort: string) => tierExposesCaps(effort) || effort === "standard";

/**
 * Which tiers can actually GRANT network access. `Trusted` is the lowest tier `AgentAutonomyPolicy.Derive` gives
 * `AgentNetworkAccess.On`, and a launch only reaches it where the effort tier's bounds preset admits it: the
 * Standard and Deep presets cap autonomy at `Trusted`, Quick at `Standard`. So Quick is severed by policy, and
 * `auto` is out because the tier it resolves to isn't known here — offering a control whose answer the router
 * might discard is the exact dishonesty this replaces.
 */
export const tierGrantsNetwork = (effort: string) => effort === "standard" || effort === "deep";

/** The tiers ASCENDING by privilege — the order `AgentAutonomyLevel` declares, which is what makes the ceiling clamp
 *  on BOTH sides of the wire a plain "take the lower one" (`AgentAutonomyPolicy.Clamp` is `Math.min` over these). */
const AUTONOMY_TIERS = ["Confined", "Standard", "Trusted", "Unleashed"];

/** The lower (less privileged) of two tiers; an unrecognised tier yields the other, never an escalation. */
const lowerTier = (a: string, b: string) => {
  const [ra, rb] = [AUTONOMY_TIERS.indexOf(a), AUTONOMY_TIERS.indexOf(b)];
  if (ra < 0) return b;
  if (rb < 0) return a;
  return ra <= rb ? a : b;
};

/**
 * The ceiling this launch actually runs under — what its agents are ALLOWED to ask for. Two bounds compose, exactly
 * as the backend composes them:
 *
 * 1. the effort preset's own ceiling (`Trusted` where {@link tierGrantsNetwork}, else `Standard`), and
 * 2. the Coordination tab's "Autonomy ceiling", TIGHTEN-ONLY (`EffortRouter.TightenCeiling`) — and only on the tiers
 *    that actually SEND it, since `buildLaunchInput` omits the field elsewhere.
 *
 * Ignoring (2) is what let the composer read "Network: on (Trusted)" for a Deep launch whose own ceiling override
 * said Standard — the wire carried both, the server clamped, and the run got no network.
 */
export const routeCeiling = (effort: string, autonomyCeiling = "") =>
  lowerTier(tierGrantsNetwork(effort) ? "Trusted" : "Standard", tierExposesCaps(effort) ? autonomyCeiling : "");

/**
 * The autonomy tier a launch actually SENDS: the request clamped to {@link routeCeiling}, mirroring
 * `TaskLaunchService.ClampAutonomy`. The wire must carry the posture the operator can SEE, never a request the run
 * silently drops — so `Trusted` falls back on a tier whose ceiling cannot grant network (which keeps a choice made
 * on Deep from riding along after a switch to Fast), and equally when the operator's own ceiling forbids it.
 */
export const effectiveAutonomy = (autonomy: string, effort: string, autonomyCeiling = "") =>
  lowerTier(autonomy, routeCeiling(effort, autonomyCeiling));

/** Mirrors `AgentAutonomyPolicy.Derive`: `Trusted` is the lowest tier granted `AgentNetworkAccess.On`. */
const tierHasNetwork = (tier: string) => tier === "Trusted" || tier === "Unleashed";

/** The qualifier every "off" posture carries — mirrors `AgentAutonomyPolicy.ConfinementCaveat`. The tier's Network.Off
 *  becomes a severed namespace only where the runner rewrites the command through bubblewrap, and the setting that
 *  would refuse an unconfinable host (`Sandbox:RequireConfinement`) is committed OFF. */
export const NETWORK_CONFINEMENT_CAVEAT = " — severed only where the sandbox confines";

/**
 * The run's effective network posture in one sentence — a MIRROR of `AgentAutonomyPolicy.DescribeNetwork`, which
 * authors the same sentence for the run's journal. The composer states it BEFORE a run exists, so it cannot read the
 * backend's words off the wire and necessarily duplicates them; `networkPosture.fixture.json` is the committed
 * fixture BOTH stacks assert on, so neither wording can move without the other's test going red.
 *
 * `deploymentCeiling` is this host's own bound (`Sandbox:MaxAutonomy`), reported by the route preview. It is named
 * FIRST when it binds, because it is the one bound the operator cannot lift by choosing a different effort tier.
 * Blank means "not reported yet" — the sentence then says only what the route can account for, never a guess.
 */
export const describeNetwork = (effective: string, ceiling: string, deploymentCeiling = "") => {
  if (tierHasNetwork(effective)) return `Network: on (${effective})`;

  if (deploymentCeiling && !tierHasNetwork(deploymentCeiling))
    return `Network: clamped off by deployment ceiling (${deploymentCeiling})${NETWORK_CONFINEMENT_CAVEAT}`;

  if (!tierHasNetwork(ceiling)) return `Network: clamped off by policy (ceiling ${ceiling})${NETWORK_CONFINEMENT_CAVEAT}`;

  return `Network: off (${effective})${NETWORK_CONFINEMENT_CAVEAT}`;
};

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
    autonomy: effectiveAutonomy(state.autonomy, state.effort, state.autonomyCeiling),
    model: state.model || null,
    harness: state.harness || null,
    agentDefinitionId: state.agentDefinitionId || null,
    runnerKind: state.runnerKind || null,
    modelCredentialId: state.modelCredentialId || null,
    modelCredentialModelId: state.modelCredentialModelId || null,
  };

  // The shape a prior preview classified, echoed back so an explicit tier keeps it (the backend ignores it on auto).
  if (state.deliverableShape) input.deliverableShape = state.deliverableShape;

  // Continue an existing session as its next turn (the session-room composer sets it); unset ⇒ a fresh session opens.
  if (state.sessionId) input.sessionId = state.sessionId;

  const relatedRepositories = buildRelatedRepositories(state.workspace, primary);
  if (relatedRepositories) input.relatedRepositories = relatedRepositories;

  // Working-dir mode is an agent-setup knob (all tiers), inert on a single-repo run. "auto" is the default ⇒ omitted ⇒
  // byte-identical; "workspace"/"primary" are sent so a multi-repo run anchors the cwd where the operator asked.
  if (state.cwdMode && state.cwdMode !== "auto") input.workingDirMode = state.cwdMode;

  // Nullable booleans are three-state in the UI: inherit omits; on/off are both explicit and survive profile/default
  // resolution. This prevents a visible "Off" from being silently reinterpreted as ambient "On" downstream.
  if (state.enableMcp !== "inherit") input.enableMcp = state.enableMcp === "on";

  // The tool allow-list (a Claude-only capability filter). Empty ⇒ omitted ⇒ the harness default (all tools) ⇒
  // byte-identical; a non-empty pick is sent verbatim. An agent-setup knob ⇒ all tiers.
  if (state.tools.length) input.allowedTools = [...state.tools];

  if (state.pushBranch !== "inherit") input.pushBranch = state.pushBranch === "on";

  // The per-agent wall-clock — sent on ALL tiers (a per-agent setting, unlike the deep/auto-gated caps). The default
  // is "3600" (1h) for every tier EXCEPT Deep, which defaults to "7200" (2h) — matching
  // TaskLaunchService.DefaultTimeoutSeconds' deep-only DeepAgentTimeoutSeconds fallback. The tier's own default is
  // OMITTED so an untouched launch stays byte-identical to the backend default; "0" = No limit (unbounded — the
  // backend maps 0 → no wall-clock) is sent explicitly, as is any other non-default value.
  const defaultTimeLimit = state.effort === "deep" ? 7200 : 3600;
  const timeLimit = Number.parseInt(state.timeLimit, 10);
  if (Number.isFinite(timeLimit) && timeLimit >= 0 && timeLimit !== defaultTimeLimit) input.timeoutSeconds = timeLimit;

  const caps = buildCaps(state, tierExposesCaps(state.effort), tierExposesBudget(state.effort));
  if (caps) input.caps = caps;

  // The agent model pool is a supervisor-lane bound (inert on a single-agent run), and the Coordination tab that
  // sets it is only shown on deep/auto — so gate it the same way as caps. Empty ⇒ omit (all the team's models).
  if (tierExposesCaps(state.effort) && state.agentModels.length) input.allowedModelIds = [...state.agentModels];

  // The agent (persona) pool — same deep/auto gating as the model pool; empty ⇒ omit (all the team's personas).
  if (tierExposesCaps(state.effort) && state.agentPool.length) input.allowedAgentDefinitionIds = [...state.agentPool];

  // The autonomy ceiling is a Coordination knob (deep/auto only); "" means Inherit the preset ⇒ omit the key.
  if (tierExposesCaps(state.effort) && state.autonomyCeiling) input.autonomyCeiling = state.autonomyCeiling;

  if (tierExposesCaps(state.effort) && state.integrateBranches !== "inherit") input.integrateBranches = state.integrateBranches === "on";

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

/**
 * B1: the ROUTE-PREVIEW payload — DERIVED from {@link buildLaunchInput}, never assembled separately. The preview
 * only means anything if it routes the launch the operator is actually about to send, and every field below
 * genuinely moves the answer: `effort` picks the tier (or asks the classifier), `caps` + `autonomyCeiling` merge
 * onto the preset's bounds, `surfaceKind` selects the seed provider, and repo / branch / related repos shape the
 * seed the classifier reads and the scope guard validates. Sending a bare goal previewed a DIFFERENT launch.
 *
 * <p>Execution overrides (model, harness, persona, runner, review modes, timeouts, quality tier) are absent
 * because the router never reads them — including them would imply this predicts more than it does. `recipe` is
 * absent because the composer has no control that pins one; the backend command still accepts it.</p>
 */
export function buildRoutePreviewInput(state: LaunchFormState): RoutePreviewInput {
  const launch = buildLaunchInput(state);

  const input: RoutePreviewInput = {
    taskText: launch.taskText,
    surfaceKind: launch.surfaceKind,
  };

  if (launch.repositoryId) input.repositoryId = launch.repositoryId;
  if (launch.baseBranch) input.baseBranch = launch.baseBranch;
  if (launch.effort) input.effort = launch.effort;
  if (launch.relatedRepositories) input.relatedRepositories = launch.relatedRepositories;
  if (launch.caps) input.caps = launch.caps;
  if (launch.autonomyCeiling) input.autonomyCeiling = launch.autonomyCeiling;
  if (launch.deliverableShape) input.deliverableShape = launch.deliverableShape;

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

/** The Coordination "Limits" + "Budget" as the backend `caps` (TaskCapsOverride). Each cap is included only when
 *  set to a real value AND its own tier gate allows it — the budget's gate is wider than the rest, because the
 *  standard lane enforces a budget but has no use for the supervisor-lane limits. All-unset ⇒ undefined. */
function buildCaps(state: LaunchFormState, exposeLimits: boolean, exposeBudget: boolean) {
  const caps: NonNullable<LaunchTaskInput["caps"]> = {};

  const maxParallelism = exposeLimits ? posIntCap(state.maxParallel) : undefined;
  if (maxParallelism !== undefined) caps.maxParallelism = maxParallelism;

  // Rounds + total-spawn are NOT operator knobs — a supervised run loops until done, bounded by cost + no-progress +
  // the model's stop (the round/total ceilings survive only as hidden backend back-stops). So the launch never sends them.

  if (exposeBudget && state.budget !== "none") {
    const cost = Number(state.budget);
    if (Number.isFinite(cost) && cost > 0) caps.maxCostUsd = cost;
  }

  return Object.keys(caps).length ? caps : undefined;
}
