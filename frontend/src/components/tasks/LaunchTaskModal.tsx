import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import type { RoutePlan, TaskSpecSuggestion, TaskSurfaceKind } from "@/api/tasks";
import { buildLaunchInput, buildRoutePreviewInput, DEFAULT_ACCEPTANCE, describeNetwork, effectiveAutonomy, NETWORK_CONFINEMENT_CAVEAT, routeCeiling, tierGrantsNetwork, type LaunchBooleanOverride, type LaunchFormState } from "@/lib/launchInput";
import { presetOf, QUALITY_PRESETS, type QualityTier } from "@/lib/qualityPresets";
import { Combo, type Option } from "@/components/common/Combo";
import { DecisionLadderDiagram, EvaluationPipelineDiagram, HelpTip, PlanCriticDiagram } from "@/components/tasks/LaunchHelp";
import { usePopover } from "@/components/common/usePopover";
import { Ic } from "@/_imported/ai-code-space/icons";
import { useAgentDefinitions, useHarnesses } from "@/hooks/use-agents";
import { useCredentialedModels } from "@/hooks/use-model-credentials";
import { useRepositories, useRepositoryBranches } from "@/hooks/use-repositories";
import { useRoutePreview } from "@/hooks/use-route-preview";
import { useSpecPreview } from "@/hooks/use-spec-preview";
import { useLaunchTask } from "@/hooks/use-tasks";

const BOOLEAN_OVERRIDE_OPTIONS: Option[] = [
  { value: "inherit", label: "Inherit", desc: "Use the effective profile or deployment default" },
  { value: "on", label: "On", desc: "Explicitly enable for this run" },
  { value: "off", label: "Off", desc: "Explicitly disable for this run" },
];

/** Caller-supplied prefill. The component shape is INVARIANT across surfaces (Repository / PR / Issue /
 *  Chat / Workflow / Run failure / Decision queue) — only this prop and `surface` differ. */
export interface LaunchTaskAutofill {
  taskText?: string;
  repositoryId?: string;
  repositoryLabel?: string;
  baseBranch?: string;
  effort?: string;
  autonomy?: string;
  /** Pin the run to a specific agent persona (its AgentDefinition id). The Agents roster's per-row "Launch task"
   *  sets it so the generic composer opens with that persona injected — no bespoke modal, just this prefill. */
  agentDefinitionId?: string;
  linkedEntity?: { label: string; url?: string };
}

export interface LaunchTaskModalProps {
  surface: TaskSurfaceKind;
  autofill?: LaunchTaskAutofill;
  onClose: () => void;
  /** Receives the started run's id so the caller can navigate to its phase tree. */
  onLaunched?: (runId: string) => void;
  /** Render the SAME composer DOCKED inline (no portal / mask / modal chrome) — for the Session room's bottom bar.
   *  The box floats on its own (its border + shadow); the host provides the surrounding layout. */
  inline?: boolean;
  /** When set, the launch CONTINUES this work session as its next turn (threaded into the launch input). */
  sessionId?: string;
  /** Override the input placeholder — e.g. the Session room's "Reply to continue this session…". */
  placeholder?: string;
}

interface WorkspaceRepo { repositoryId: string; branch: string; access: "write" | "read"; alias: string; isPrimary: boolean }

const EFFORT_OPTS: { v: string; l: string; d: string; tip?: string }[] = [
  { v: "auto", l: "Auto", d: "CodeSpace picks the depth" },
  { v: "quick", l: "Fast", d: "one agent, quick pass" },
  { v: "standard", l: "Standard", d: "Split into parallel subtasks", tip: "Planner creates subtasks, agents run them in parallel, then results are combined." },
  { v: "deep", l: "Deep", d: "supervisor coordinates agents", tip: "A supervisor can spawn agents, inspect results, ask for decisions, and retry." },
];
const PERMS = [
  { v: "Confined", d: "read-only · no network" },
  { v: "Standard", d: "workspace edits · no network" },
  { v: "Trusted", d: "workspace edits · network" },
  { v: "Unleashed", d: "controlled runner · high trust" },
];
// Which of PERMS a launch can actually reach — the composer never offers a tier the backend would silently reduce
// (TaskLaunchService.ClampAutonomy clamps every request to the route ceiling). Defined as "the shared clamp leaves
// it alone", so it tracks BOTH bounds the ceiling is made of: the effort preset's own (Standard/Deep cap at Trusted,
// the only tier granting network; Quick/Auto at Standard) and the Coordination tab's tighten-only override. Unleashed
// is reachable nowhere — no preset names it, so it stays an option-shape entry only.
const reachablePerms = (effort: string, autonomyCeiling = "") =>
  PERMS.filter(p => effectiveAutonomy(p.v, effort, autonomyCeiling) === p.v);

/** The consequence of On, stated plainly — no reassurance, no policy that does not exist. `On` grants the HOST
 *  network to every agent the run spawns, and the model credential the agents run on is in their environment. */
const NETWORK_ON_CONSEQUENCE = "Every agent this run spawns reaches the host network — the public internet, your LAN, and cloud metadata endpoints. The model credential is present in the agent's environment.";

/** The Network row's two answers. Off is qualified the same way the run's journal qualifies it: the tier's Off is a
 *  permission, and it becomes severed egress only where the sandbox actually confines. */
const NETWORK_OPTIONS: Option[] = [
  { value: "off", label: "Off", desc: `The agents are granted no network${NETWORK_CONFINEMENT_CAVEAT}` },
  { value: "on", label: "On", desc: NETWORK_ON_CONSEQUENCE },
];

/**
 * The one generic "Launch a task" composer — a minimal Copilot/Gemini-style box: a task input with the
 * Permission tier + Repositories multi-select (per-repo branch inline) bottom-left, and a single
 * Model·Effort selector bottom-right. Every dropdown is the in-house warm-theme `Combo` (no native
 * selects). "Customize" expands in place into Supervisor (on Deep) + Advanced execution / safety. WIRED
 * fields drive a real `POST /api/workflows/runs`; extra repos, supervisor config and safety toggles are design-ahead.
 */
export function LaunchTaskModal({ surface, autofill, onClose, onLaunched, inline = false, sessionId, placeholder: placeholderProp }: LaunchTaskModalProps) {
  const [taskText, setTaskText] = useState(autofill?.taskText ?? "");
  const [workspace, setWorkspace] = useState<WorkspaceRepo[]>(() =>
    autofill?.repositoryId
      ? [{ repositoryId: autofill.repositoryId, branch: autofill.baseBranch ?? "", access: "write", alias: (autofill.repositoryLabel ?? "").split("/").pop() || "repo", isPrimary: true }]
      : [],
  );
  const [effort, setEffort] = useState(autofill?.effort ?? "auto");
  const [autonomy, setAutonomy] = useState(autofill?.autonomy ?? "Standard");
  const [model, setModel] = useState("");
  const [modelCredentialId, setModelCredentialId] = useState("");
  const [harness, setHarness] = useState("");
  const [agentDefinitionId, setAgentDefinitionId] = useState(autofill?.agentDefinitionId ?? "");
  const [runnerKind, setRunnerKind] = useState("");
  const [expanded, setExpanded] = useState(false);
  const [menu, setMenu] = useState<null | "perm" | "repos" | "mr">(null);
  const [effortOpen, setEffortOpen] = useState(false);
  /** The deliverable shape carried over from the confirm card the operator answered, pinned to the text it was classified for. */
  const [confirmedShape, setConfirmedShape] = useState<{ text: string; shape: string } | null>(null);
  const [repoSearch, setRepoSearch] = useState("");
  const [customizeTab, setCustomizeTab] = useState<"execution" | "planning" | "supervisor" | "safety" | "evaluation">("execution");
  const [acceptDraft, setAcceptDraft] = useState("");
  const [checksDraft, setChecksDraft] = useState("");

  // Per-row tier honesty (the Coordination tab's lt3-cdisabled pattern, at row grain — these two tabs mix tiers):
  // an off-tier control renders as a muted read-only row instead of an armed switch the wire would silently drop.
  const planCapable = effort !== "quick";   // every tier that authors a plan can park on it + critique it

  // TIER-AWARE Gate copy: the same "Plan critic = Gate" is a SOFT annotate-for-the-human on Standard (concerns land
  // as risks on the plan / confirm card, the plan is never discarded) but the HARD decision ladder on Deep (a flagged
  // plan does not execute — self-revise → re-review → escalate). Same wire value, different consequence — say so.
  const planCriticOpts: Option[] = [
    { value: "None", label: "Off", desc: "No plan review — fine for small tasks, or when you read the plan yourself" },
    effort === "deep"
      ? { value: "Gate", label: "Gate — block until the plan passes", desc: "Hard gate on Deep: a flagged plan does not execute — self-revise, re-review, then escalate to you" }
      : { value: "Gate", label: "Gate — annotate concerns onto the plan", desc: "Pick when you will look: concerns + evidence land on the plan and the confirm card for your call" },
    { value: "Improve", label: "Improve — one revision against the critique", desc: "Pick for unattended runs: one extra planner call folds the critique in automatically" },
  ];
  // Design-ahead Customize config (interactive UI state; not yet sent to the launch command).
  const [cfg, setCfg] = useState({
    pushBranch: "inherit" as LaunchBooleanOverride, tools: [] as string[], enableMcp: "inherit" as LaunchBooleanOverride, cwdMode: "auto",
    agentModels: [] as string[], agentPool: [] as string[],
    maxParallel: "5", budget: "none",
    integrateBranches: "inherit" as LaunchBooleanOverride, autonomyCeiling: "",
    acceptance: [...DEFAULT_ACCEPTANCE], acceptanceChecks: [] as string[],
    timeLimit: "3600",
    requirePlanConfirmation: false, plannerReview: "None",
    decisionReview: "None", outputReview: "None", reviewerModel: "", reviseRounds: "", reviewerAgent: false,
  });
  const setC = (p: Partial<typeof cfg>) => setCfg(c => ({ ...c, ...p }));

  // Network is NOT an independent axis: `Trusted` IS "workspace edits + network" (AgentAutonomyPolicy.Derive), so
  // the Permissions row and the Network row read and write ONE value and can never disagree. Shown EFFECTIVE, never
  // raw — a Trusted pick made on Deep has to visibly fall back to Standard when the tier changes to Fast, AND when
  // the Coordination tab's own Autonomy ceiling (which rides the same wire and tightens the route) forbids it. That
  // is what the wire does too (buildLaunchInput), and the composer must show what actually launches.
  const ceilingShown = routeCeiling(effort, cfg.autonomyCeiling);
  const autonomyShown = effectiveAutonomy(autonomy, effort, cfg.autonomyCeiling);
  const networkOn = autonomyShown === "Trusted";
  // The one honest consequence line: the SAME sentence AgentAutonomyPolicy.DescribeNetwork will write into the run's
  // journal (shared words, pinned by networkPosture.fixture.json), plus what On actually costs. Off-tier keeps its
  // own copy — there the resolved ceiling is not known yet, so it names the reason instead of claiming one.
  const networkPosture = !tierGrantsNetwork(effort)
    ? `Network: off${NETWORK_CONFINEMENT_CAVEAT} — this tier's ceiling is Standard, which has no network. Switch Effort to Standard or Deep to choose.`
    : `${describeNetwork(autonomyShown, ceilingShown)}.${networkOn ? ` ${NETWORK_ON_CONSEQUENCE}` : ""}`;

  // "Time limit" defaults to 1h everywhere EXCEPT Deep, which defaults to 2h (matching
  // TaskLaunchService.DeepAgentTimeoutSeconds) — untouched, the row shows/sends that tier's OWN default (never
  // touched ⇒ omitted from the wire, byte-identical); once the operator picks a value explicitly, it sticks
  // across an effort change instead of silently snapping back to a tier default.
  const [timeLimitTouched, setTimeLimitTouched] = useState(false);
  const timeLimitDefault = effort === "deep" ? "7200" : "3600";
  const effectiveTimeLimit = timeLimitTouched ? cfg.timeLimit : timeLimitDefault;
  const timeLimitOpts: Option[] = [
    { value: "1800", label: "30 minutes" },
    { value: "3600", label: effort === "deep" ? "1 hour" : "1 hour (default)" },
    { value: "7200", label: effort === "deep" ? "2 hours (default)" : "2 hours" },
    { value: "0", label: "No limit" },
  ];
  // P3.2: the picked Quality tier — tracked independently of `cfg`'s knob values, NOT re-derived via `presetOf`.
  // Hand-editing a knob after picking Delivery must not quietly drop the mandate back to Prototype.
  const [tier, setTier] = useState<QualityTier>("Prototype");
  const resetTab = () => {
    if (customizeTab === "execution") { setAgentDefinitionId(""); setHarness(""); setModel(""); setModelCredentialId(""); setRunnerKind(""); setC({ pushBranch: "inherit", tools: [], enableMcp: "inherit", cwdMode: "auto" }); }
    else if (customizeTab === "planning") setC({ requirePlanConfirmation: false, plannerReview: "None", reviewerModel: "" });
    else if (customizeTab === "supervisor") setC({ agentModels: [], agentPool: [], maxParallel: "5", budget: "none", integrateBranches: "inherit", autonomyCeiling: "", decisionReview: "None" });
    else if (customizeTab === "evaluation") setC({ acceptance: [...DEFAULT_ACCEPTANCE], acceptanceChecks: [], outputReview: "None", reviseRounds: "", reviewerAgent: false });
    else { setTimeLimitTouched(false); setC({ timeLimit: "3600" }); }
  };

  const repos = useRepositories();
  const harnesses = useHarnesses();
  const credModels = useCredentialedModels();
  const personas = useAgentDefinitions();
  const launch = useLaunchTask();

  // P5-7 spec preview: once the goal settles, the backend compiles it into contract suggestions the card below
  // the box pre-fills. Everything here is display state — applying writes into the SAME cfg fields the
  // Evaluation tab edits (no parallel state), dismissal is keyed to the suggestion's content so an edited goal
  // that produces a NEW suggestion un-dismisses, and a null suggestion renders nothing at all.
  const spec = useSpecPreview(taskText, (workspace.find(r => r.isPrimary) ?? workspace[0])?.repositoryId);
  const specKey = spec.suggestion ? JSON.stringify(spec.suggestion) : "";
  const [specDismissedKey, setSpecDismissedKey] = useState("");
  // Applied state is KEYED to the suggestion's content and derived at render (never reset via an effect — the
  // lint-enforced no-sync-setState-in-effect rule): a NEW suggestion reads un-applied automatically.
  const [specAppliedFor, setSpecAppliedFor] = useState<{ key: string; checks: boolean; criteria: boolean }>({ key: "", checks: false, criteria: false });
  const specApplied = specAppliedFor.key === specKey ? specAppliedFor : { checks: false, criteria: false };
  const showSpecCard = !!spec.suggestion && specKey !== specDismissedKey;
  const applySpecChecks = () => {
    if (!spec.suggestion?.acceptanceChecks.length) return;
    setC({ acceptanceChecks: [...spec.suggestion.acceptanceChecks] });
    setSpecAppliedFor(p => ({ key: specKey, checks: true, criteria: p.key === specKey && p.criteria }));
  };
  const applySpecCriteria = () => {
    if (!spec.suggestion?.acceptanceCriteria.length) return;
    setC({ acceptance: [...new Set([...cfg.acceptance, ...spec.suggestion.acceptanceCriteria])] });
    setSpecAppliedFor(p => ({ key: specKey, checks: p.key === specKey && p.checks, criteria: true }));
  };

  const closeMenu = () => { setMenu(null); setEffortOpen(false); };

  const flyTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const openFly = () => { if (flyTimer.current) clearTimeout(flyTimer.current); setEffortOpen(true); };
  const closeFlySoon = () => { flyTimer.current = setTimeout(() => setEffortOpen(false), 130); };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      if (menu) closeMenu(); else onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, menu]);

  // Close the open bar menu on any click outside a popover / flyout / pill. Robust across the modal's
  // CSS transform (which breaks position:fixed masks) — the earlier mask sat behind the box and couldn't
  // be clicked, so the dropdown wouldn't close.
  useEffect(() => {
    if (!menu) return;
    const onDown = (e: MouseEvent) => {
      const t = e.target as HTMLElement;
      if (t.closest(".lt3-pop") || t.closest(".lt3-flyout") || t.closest(".lt3-pill")) return;
      closeMenu();
    };
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [menu]);

  useEffect(() => () => { if (flyTimer.current) clearTimeout(flyTimer.current); }, []);

  const repoName = (id: string) => repos.data?.find(r => r.id === id)?.fullPath ?? autofill?.repositoryLabel ?? id;
  const primary = workspace.find(r => r.isPrimary) ?? workspace[0];
  const reposLabel = workspace.length === 0 ? "Repositories" : workspace.length === 1 ? repoName(workspace[0].repositoryId) : `${workspace.length} repositories`;

  // Resolve the picked (model, credential) to its concrete row id so the backend can pin the supervisor brain
  // (Deep) / the agent model (single-agent) by row, not guess between two credentials of the same model name.
  const modelCredentialModelId = credModels.data?.find(o => o.modelId === model && o.credentialId === modelCredentialId)?.rowId ?? "";

  // ONE snapshot of the form, shared by the launch and the route preview. Two snapshots would let the preview
  // predict a launch that differs from the one the button sends — the whole point of previewing.
  const formState: LaunchFormState = {
    taskText, surface, sessionId, workspace, effort, autonomy: autonomyShown, model, modelCredentialId, modelCredentialModelId, harness, agentDefinitionId, runnerKind,
    // Only while the confirmed shape still belongs to the text on screen — editing the task after confirming makes
    // the echo stale, and a stale shape is worse than none (it would project the OLD task's shape onto a new one).
    deliverableShape: confirmedShape?.text === taskText.trim() ? confirmedShape.shape : undefined,
    cwdMode: cfg.cwdMode, enableMcp: cfg.enableMcp, tools: cfg.tools, pushBranch: cfg.pushBranch,
    maxParallel: cfg.maxParallel, budget: cfg.budget,
    agentModels: cfg.agentModels, agentPool: cfg.agentPool, autonomyCeiling: cfg.autonomyCeiling, timeLimit: effectiveTimeLimit,
    integrateBranches: cfg.integrateBranches, acceptanceCriteria: cfg.acceptance, acceptanceChecks: cfg.acceptanceChecks,
    requirePlanConfirmation: cfg.requirePlanConfirmation, plannerReview: cfg.plannerReview,
    decisionReview: cfg.decisionReview, outputReview: cfg.outputReview, reviewerModel: cfg.reviewerModel, reviseRounds: cfg.reviseRounds, reviewerAgent: cfg.reviewerAgent,
    tier,
  };

  // B1 route preview: on the AUTO tier the backend tells us where this launch WOULD go before it goes anywhere.
  // The router has always built a confirm card for a low-confidence or risky-side-effect auto route — nothing ever
  // showed it, so a task flagged for delete/drop/migrate/deploy/production/secrets was routed and STARTED with no
  // human gate. Asking is free: the endpoint opens no session and stages no run. An explicit tier is already the
  // operator's decision, so the preview is not asked for one at all (null disables it).
  const routePreview = useRoutePreview(effort === "auto" ? buildRoutePreviewInput(formState) : null);
  const routeCard = routePreview.route?.needsConfirmCard ? routePreview.route : null;
  // The gate: an auto launch WAITS on the operator's answer. Answering means picking a tier, which then rides the
  // wire as an EXPLICIT effort — the confirmation is the tier itself, never a separate flag the backend must trust.
  const routeConfirmPending = !!routeCard;
  // …and it waits on the QUESTION too. Gating on the card alone leaves Launch live through the debounce window and
  // the in-flight request, so a risky goal typed and sent inside ~1-3s would start unconfirmed — the card would
  // arrive after the run did. A settled failure counts as answered, so an outage never wedges the button.
  const routeUnanswered = !routePreview.answered;
  // Answering the card picks a TIER, which rides the wire as an explicit effort and short-circuits the classifier —
  // so the shape the card was raised about has to ride along too, or every confirmed launch silently reverts to the
  // coding projection. Stored with the text it was classified for; see formState for the staleness guard.
  const confirmEffort = (mode: string) => {
    const shape = routeCard?.deliverableShape;
    setConfirmedShape(shape ? { text: taskText.trim(), shape } : null);
    setEffort(mode);
    closeMenu();
  };

  const effLabel = EFFORT_OPTS.find(e => e.v === effort)?.l ?? "Auto";
  const modelLabel = model || "Auto";
  const comboLabel = (modelLabel === "Auto" && effLabel === "Auto") ? "Auto" : `${modelLabel} · ${effLabel}`;
  const placeholder = placeholderProp ?? (effort === "deep" ? "Describe a goal to coordinate…" : "Describe a task…");

  // The single Model chip is the "primary reasoning model"; its role — and so its label/explanation —
  // depends on the effort tier. In Deep it is the supervisor's brain (agents draw from the pool).
  const modelRole =
    effort === "deep" ? { title: "Supervisor brain model", note: "Runs the supervisor. Agents draw from the model pool." }
      : effort === "quick" ? { title: "Agent model", note: "The model the single agent runs on." }
        : effort === "standard" ? { title: "Default model", note: "Default for the planner, agents, and summary." }
          : { title: "Reasoning model", note: "Primary model — its role follows the effort tier." };

  const sortedRepos = useMemo(() => {
    const sel = new Set(workspace.map(r => r.repositoryId));
    const q = repoSearch.trim().toLowerCase();
    return (repos.data ?? [])
      .filter(r => r.fullPath.toLowerCase().includes(q))
      .sort((a, b) => (sel.has(b.id) ? 1 : 0) - (sel.has(a.id) ? 1 : 0));
  }, [repos.data, workspace, repoSearch]);

  const missing: string[] = [];
  if (!taskText.trim()) missing.push("a task");
  // A repository is required only for the `repo` surface (a change anchored to a codebase). The `chat` surface's
  // goal IS the task text — the backend runs it repo-less (a research / answer task, or an agent launched from the
  // roster) — so requiring a repo there would dead-end the launch on a workspace the run never needs.
  if (surface === "repo" && !primary?.repositoryId) missing.push("a repository");
  // P3.2: Delivery/Unattended MANDATES an executable acceptance check — the backend rejects a Deep launch that
  // claims this tier without one, so catch it here instead of letting the operator hit a server-side error after
  // submit. Standard is excluded: it verifies per item via the plan's own contracts and never sends this field for
  // ANY tier (the same `effort !== "standard"` gate the Acceptance-checks row itself is already shown/sent under).
  if (tier !== "Prototype" && effort !== "standard" && cfg.acceptanceChecks.length === 0) missing.push("an acceptance check");
  // B1: an auto route the router wants confirmed BLOCKS the launch until the operator picks a tier, and an auto
  // route not yet ANSWERED blocks it until the router has spoken. This is the one place the confirm card stops
  // being decoration — a risky auto-classified task can no longer start unattended, or beat its own preview.
  const canLaunch = missing.length === 0 && !routeConfirmPending && !routeUnanswered && !launch.isPending;

  // A disabled send button must say WHY. Missing inputs first (the operator can act on those immediately), then
  // the confirm card, then the still-open question — never a bare disabled button the operator reads as broken.
  const launchBlockedReason = canLaunch ? "Launch"
    : missing.length ? `Add ${missing.join(" and ")}`
      : routeConfirmPending ? "Confirm the effort above to launch"
        : routeUnanswered ? "Checking where this task will run…"
          : "Launching…";

  const toggleRepo = (id: string) => {
    const short = repoName(id).split("/").pop() || "repo";
    setWorkspace(w => {
      const without = w.filter(r => r.repositoryId !== id);
      const next = without.length === w.length
        ? [...w, { repositoryId: id, branch: "", access: "read" as const, alias: short, isPrimary: false }]
        : without;
      return next.map((r, i) => ({ ...r, isPrimary: i === 0, access: i === 0 ? "write" as const : r.access }));
    });
  };
  const patchRepo = (id: string, p: Partial<WorkspaceRepo>) => setWorkspace(w => w.map(r => r.repositoryId === id ? { ...r, ...p } : r));
  const repoMeta = (id: string) => workspace.find(r => r.repositoryId === id);

  const submit = () => {
    if (!canLaunch) return;
    // The SAME form snapshot the route preview was built from, so the launch cannot differ from what was previewed.
    launch.mutate(buildLaunchInput(formState), { onSuccess: res => onLaunched?.(res.runId) });
  };

  // Surface the model's intelligence in the picker: the EFFECTIVE capability tier (so the operator sees how auto ranks
  // it) + an "offline" mark for a self-hosted gateway the availability probe found unreachable.
  const modelDesc = (o: { provider: string; credentialName: string; tier?: string | null; available?: boolean | null }) =>
    `${o.provider} · ${o.credentialName}${o.tier && o.tier !== "Unknown" ? ` · ${o.tier}` : ""}${o.available === false ? " · offline" : ""}`;

  const harnessOpts: Option[] = [{ value: "", label: "Auto" }, ...(harnesses.data ?? []).map(h => ({ value: h.kind, label: h.kind }))];
  const runnerOpts: Option[] = [{ value: "", label: "Local sandbox" }];
  const modelOpts: Option[] = [{ value: "", label: "Auto" }, ...(credModels.data ?? []).map(o => ({ value: o.modelId, label: o.modelId, desc: modelDesc(o) }))];

  // The Agent pool (cfg.agentPool) limits which agents the run may use — empty means any suitable agent.
  // The Agent-setup "Agent" then offers only those (plus Auto / inline).
  const allPersonas = personas.data ?? [];
  const poolPersonas = cfg.agentPool.length ? allPersonas.filter(p => cfg.agentPool.includes(p.id)) : allPersonas;
  const agentDefOpts: Option[] = [{ value: "", label: "Auto / inline" }, ...poolPersonas.map(p => ({ value: p.id, label: p.name }))];
  const agentPoolLabel = cfg.agentPool.length ? `${cfg.agentPool.length} agent${cfg.agentPool.length > 1 ? "s" : ""}` : "Any suitable agent";
  const togglePoolAgent = (id: string) => {
    const next = cfg.agentPool.includes(id) ? cfg.agentPool.filter(a => a !== id) : [...cfg.agentPool, id];
    setC({ agentPool: next });
    if (agentDefinitionId && next.length && !next.includes(agentDefinitionId)) setAgentDefinitionId("");
  };

  // The Agent-model pool (cfg.agentModels) limits which models agents may use — empty means all eligible.
  // The Agent-setup "Agent model" and the primary chip then offer only those models (plus Auto). In Deep
  // the primary model is the supervisor brain (unconstrained) and agents draw from the pool, so it's Auto.
  const allModels = credModels.data ?? [];
  const poolModels = cfg.agentModels.length ? allModels.filter(o => cfg.agentModels.includes(o.rowId)) : allModels;
  const agentModelOpts: Option[] = [{ value: "", label: "Auto" }, ...poolModels.map(o => ({ value: o.modelId, label: o.modelId, desc: modelDesc(o) }))];
  const menuModels = effort === "deep" ? allModels : poolModels;
  const poolLabel = cfg.agentModels.length ? `${cfg.agentModels.length} model${cfg.agentModels.length > 1 ? "s" : ""}` : "All eligible models";
  // Toggle a model ROW in the pool (keyed by the row id so two credentials exposing the same model name stay
  // distinct). Outside Deep the picked model IS the agent model, so if narrowing the pool strands its row, fall
  // back to Auto. (In Deep the model is the unconstrained supervisor brain.)
  const togglePoolModel = (rowId: string) => {
    const next = cfg.agentModels.includes(rowId) ? cfg.agentModels.filter(m => m !== rowId) : [...cfg.agentModels, rowId];
    setC({ agentModels: next });
    const selectedRowId = allModels.find(o => o.modelId === model && o.credentialId === modelCredentialId)?.rowId;
    if (effort !== "deep" && selectedRowId && next.length && !next.includes(selectedRowId)) { setModel(""); setModelCredentialId(""); }
  };
  const pickModel = (v: string) => { setModel(v); setModelCredentialId(credModels.data?.find(o => o.modelId === v)?.credentialId ?? ""); };

  // The Tools allow-list (cfg.tools) is a CLAUDE-ONLY capability filter — empty = the harness default (all tools),
  // a non-empty pick = exactly these (Custom). It is additive against a persona's tools and is NOT a write boundary
  // (use the Permissions tab's autonomy tier for read-only). The canonical PascalCase names Claude's --allowed-tools
  // matches; Codex ignores the list (it bounds the agent via its sandbox).
  const CLAUDE_TOOLS = ["Read", "Grep", "Glob", "Edit", "Write", "MultiEdit", "Bash", "WebFetch", "WebSearch", "NotebookEdit"];
  const toolsLabel = cfg.tools.length ? `${cfg.tools.length} tool${cfg.tools.length > 1 ? "s" : ""}` : "Default · all tools";
  const toggleTool = (name: string) => setC({ tools: cfg.tools.includes(name) ? cfg.tools.filter(t => t !== name) : [...cfg.tools, name] });

  // Inline (Session room) composer: the textarea grows with its content (capped), like the design — modal mode keeps its fixed min-height.
  const taRef = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    const ta = taRef.current;
    if (!ta || !inline) return;
    ta.style.height = "auto";
    ta.style.height = `${Math.min(ta.scrollHeight, 200)}px`;
  }, [taskText, inline]);

  const content = (
    <>
      {routeCard && <RouteConfirmCard route={routeCard} onPick={confirmEffort} />}
      {!routeCard && effort === "auto" && routePreview.route && <RouteHint route={routePreview.route} />}
      {!routeCard && effort === "auto" && routePreview.failed && (
        <div className="lt3-route-quiet">Route preview unavailable — launching will still pick a depth automatically.</div>
      )}

      <div className="lt3-box">
          <textarea ref={taRef} className="lt3-input" rows={inline ? 1 : 3} placeholder={placeholder} value={taskText} onChange={e => setTaskText(e.target.value)} autoFocus={!inline} />

          <div className="lt3-bar">
            <button type="button" className="lt3-pill lt3-adv" data-open={expanded} aria-expanded={expanded} title="Advanced settings — execution · supervisor · safety" onClick={() => setExpanded(v => !v)}>
              <Ic.Settings size={16} /><span>Advanced</span>
            </button>

            <div className="lt3-anchor">
              <button className="lt3-pill" title="Permission" onClick={() => setMenu(m => m === "perm" ? null : "perm")}>
                <Ic.Lock size={16} /><span>{autonomyShown}</span><Ic.ChevronDown size={14} />
              </button>
              {menu === "perm" && (
                <Pop align="left">
                  <div className="lt3-pop-t">Permission</div>
                  {reachablePerms(effort, cfg.autonomyCeiling).map(p => (
                    <button key={p.v} className="lt3-opt" data-on={autonomyShown === p.v} onClick={() => { setAutonomy(p.v); closeMenu(); }}>
                      <span className="lt3-opt-m"><span className="lt3-opt-t">{p.v}</span><span className="lt3-opt-d">{p.d}</span></span>
                      {autonomyShown === p.v && <Ic.Check size={14} />}
                    </button>
                  ))}
                </Pop>
              )}
            </div>

            <div className="lt3-anchor lt3-anchor-flex">
              <button className="lt3-pill" title="Repositories" onClick={() => setMenu(m => m === "repos" ? null : "repos")}>
                <Ic.Repo size={16} /><span>{reposLabel}</span><Ic.ChevronDown size={14} />
              </button>
              {menu === "repos" && (
                <Pop align="left" wide>
                  <div className="lt3-pop-t">Select repositories</div>
                  <input className="lt3-search" placeholder="Search" value={repoSearch} onChange={e => setRepoSearch(e.target.value)} autoFocus />
                  <div className="lt3-rlist">
                    {sortedRepos.map(r => {
                      const on = workspace.some(w => w.repositoryId === r.id);
                      return (
                        <div className="lt3-ritem" data-on={on} key={r.id}>
                          <button className="lt3-ritem-main" onClick={() => toggleRepo(r.id)}>
                            <span className="lt3-check" data-on={on}>{on && <Ic.Check size={11} />}</span>
                            <Ic.Repo size={14} /><span className="lt3-rname">{r.fullPath}</span>
                          </button>
                          {on && (
                            <div className="lt3-rmeta">
                              <BranchCombo repoId={r.id} value={repoMeta(r.id)?.branch ?? ""} onChange={b => patchRepo(r.id, { branch: b })} />
                              <Combo value={repoMeta(r.id)?.access ?? "read"} options={[{ value: "write", label: "write" }, { value: "read", label: "read" }]} onChange={a => patchRepo(r.id, { access: a as "write" | "read" })} buttonClassName="lt3-branch-btn" />
                              <input className="lt3-ralias" value={repoMeta(r.id)?.alias ?? ""} placeholder="alias" onChange={e => patchRepo(r.id, { alias: e.target.value })} onClick={e => e.stopPropagation()} />
                            </div>
                          )}
                        </div>
                      );
                    })}
                    {sortedRepos.length === 0 && <div className="lt3-rempty">No repositories</div>}
                  </div>
                </Pop>
              )}
            </div>

            <span className="lt3-spacer" />

            <div className="lt3-anchor">
              <button className="lt3-pill lt3-eff-pill" title="Model and effort" onClick={() => { setMenu(m => m === "mr" ? null : "mr"); setEffortOpen(false); }}>
                <Ic.Zap size={16} /><span>{comboLabel}</span><Ic.ChevronDown size={14} />
              </button>
              {menu === "mr" && (
                <Pop align="right">
                  <div className="lt3-pop-t">{modelRole.title}</div>
                  <div className="lt3-pop-note">{modelRole.note}</div>
                  <button className="lt3-opt" data-on={!model} onClick={() => { setModel(""); setModelCredentialId(""); closeMenu(); }}>
                    <span className="lt3-opt-m"><span className="lt3-opt-t">Auto</span><span className="lt3-opt-d">pick the best available</span></span>
                    {!model && <Ic.Check size={14} />}
                  </button>
                  {menuModels.map(o => (
                    <button key={`${o.credentialId}/${o.modelId}`} className="lt3-opt" data-on={model === o.modelId} onClick={() => { setModel(o.modelId); setModelCredentialId(o.credentialId); closeMenu(); }}>
                      <span className="lt3-opt-m"><span className="lt3-opt-t">{o.modelId}</span><span className="lt3-opt-d">{o.provider} · {o.credentialName}</span></span>
                      {model === o.modelId && <Ic.Check size={14} />}
                    </button>
                  ))}
                  {menuModels.length === 0 && <div className="lt3-rempty">{allModels.length ? "No models in the pool — Auto only." : "No credentialed models — Auto only."}</div>}
                  <div className="lt3-divider" />
                  <div className="lt3-eff-row-anchor" onMouseEnter={openFly} onMouseLeave={closeFlySoon}>
                    <button className="lt3-opt lt3-eff-row" data-open={effortOpen} aria-expanded={effortOpen} onClick={() => setEffortOpen(v => !v)}>
                      <span className="lt3-opt-m"><span className="lt3-opt-t">Effort</span></span>
                      <span className="lt3-eff-row-v">{effLabel}</span>
                      <Ic.ChevronRight size={14} />
                    </button>
                    {effortOpen && (
                      <div className="lt3-flyout" onMouseEnter={openFly} onMouseLeave={closeFlySoon}>
                        {EFFORT_OPTS.map(e => (
                          <button key={e.v} className="lt3-opt" data-on={effort === e.v} data-tip={e.tip} onClick={() => { setEffort(e.v); closeMenu(); }}>
                            <span className="lt3-opt-m"><span className="lt3-opt-t">{e.l}</span><span className="lt3-opt-d">{e.d}</span></span>
                            {effort === e.v && <Ic.Check size={14} />}
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                </Pop>
              )}
            </div>

            <button className="lt3-send" aria-label="Launch task" disabled={!canLaunch} onClick={submit} title={launchBlockedReason}>
              <SendGlyph />
            </button>
          </div>
        </div>

        {spec.loading && !showSpecCard && <div className="lt3-spec-load">Compiling contract suggestions…</div>}
        {showSpecCard && spec.suggestion && (
          <SpecSuggestionCard
            suggestion={spec.suggestion}
            grounded={spec.grounded}
            applied={specApplied}
            checksApplicable={effort !== "standard"}
            onApplyChecks={applySpecChecks}
            onApplyCriteria={applySpecCriteria}
            onDismiss={() => setSpecDismissedKey(specKey)}
          />
        )}

        {expanded && (
          <div className="lt3-cust">
            <div className="lt3-ctabs">
              <button type="button" className="lt3-ctab" data-on={customizeTab === "planning"} onClick={() => setCustomizeTab("planning")}>Planning</button>
              <button type="button" className="lt3-ctab" data-on={customizeTab === "execution"} onClick={() => setCustomizeTab("execution")}>Agent setup</button>
              <button type="button" className="lt3-ctab" data-on={customizeTab === "evaluation"} onClick={() => setCustomizeTab("evaluation")}>Evaluation</button>
              <button type="button" className="lt3-ctab" data-on={customizeTab === "supervisor"} onClick={() => setCustomizeTab("supervisor")}>Coordination</button>
              <button type="button" className="lt3-ctab" data-on={customizeTab === "safety"} onClick={() => setCustomizeTab("safety")}>Permissions</button>
              <button type="button" className="lt3-reset" onClick={resetTab}>Reset</button>
            </div>

            <div className="lt3-presets" role="radiogroup" aria-label="Quality preset">
              <span className="lt3-presets-l">Quality</span>
              {QUALITY_PRESETS.map(p => (
                <button key={p.id} type="button" className="lt3-preset" data-on={presetOf(cfg) === p.id} title={p.hint} onClick={() => { setC(p.config); setTier(p.tier); }}>{p.label}</button>
              ))}
              {presetOf(cfg) === null && <span className="lt3-preset-custom">Custom mix</span>}
            </div>

            <div className="lt3-cbody">
              {customizeTab === "execution" && <>
                <div className="lt3-cnote">Default settings for agents created during the run.</div>
                <Combo label="Agent" value={agentDefinitionId} options={agentDefOpts} onChange={setAgentDefinitionId} searchable />
                <Combo label="Harness" value={harness} options={harnessOpts} onChange={setHarness} />
                {effort === "deep"
                  ? <div className="lt3-srow lt3-srow-ro"><span className="lt3-srow-l">Agent model</span><span className="lt3-combo-v">Auto · from model pool</span></div>
                  : <Combo label="Agent model" value={model} options={agentModelOpts} onChange={pickModel} searchable />}
                <Combo label="Runner" value={runnerKind} options={runnerOpts} onChange={setRunnerKind} />
                <RowPop label="Tools" value={toolsLabel}>
                  <div className="lt3-poolhint">Restrict the agent to these tools. Leave empty for the harness default (all tools). Claude only — a capability filter, not a write boundary (use Permissions for read-only).</div>
                  <div className="lt3-rlist">
                    {CLAUDE_TOOLS.map(name => {
                      const on = cfg.tools.includes(name);
                      return (
                        <button key={name} type="button" className="lt3-opt" data-on={on} onClick={() => toggleTool(name)}>
                          <span className="lt3-check" data-on={on}>{on && <Ic.Check size={11} />}</span>
                          <span className="lt3-opt-m"><span className="lt3-opt-t">{name}</span></span>
                        </button>
                      );
                    })}
                  </div>
                </RowPop>
                <Combo label="Publish branch" value={cfg.pushBranch} options={BOOLEAN_OVERRIDE_OPTIONS} onChange={v => setC({ pushBranch: v as LaunchBooleanOverride })} />
                <Combo label="Working dir" value={cfg.cwdMode} options={[{ value: "auto", label: "Auto" }, { value: "workspace", label: "Workspace root" }, { value: "primary", label: "Primary repo" }]} onChange={v => setC({ cwdMode: v })} />
                <Combo label="Full MCP fabric" value={cfg.enableMcp} options={BOOLEAN_OVERRIDE_OPTIONS} onChange={v => setC({ enableMcp: v as LaunchBooleanOverride })} />
              </>}

              {customizeTab === "supervisor" && <>
                <div className="lt3-cnote">How Deep mode plans, delegates, reviews, and stops.</div>
                {/* Budget sits OUTSIDE the deep/auto drawer because it is no longer a deep-only concept: the
                    standard lane admits each map branch against this same ceiling. Quick stays out — a single
                    agent is already running by the time any spend happens, so there is nothing to refuse and
                    offering the control would promise an enforcement that does not exist. */}
                {effort !== "quick" && (
                  <Combo label="Budget" value={cfg.budget} options={[{ value: "none", label: "No cap" }, { value: "5", label: "$5" }, { value: "10", label: "$10" }, { value: "25", label: "$25" }]} onChange={v => setC({ budget: v })} />
                )}
                {effort === "deep" || effort === "auto" ? <>
                <Combo label="Brain model" value={model} options={effort === "deep" ? modelOpts : agentModelOpts} onChange={pickModel} searchable />
                <RowPop label="Agent model pool" value={poolLabel}>
                  <div className="lt3-poolhint">Agents draw only from these models. Leave empty to allow all eligible models.</div>
                  <div className="lt3-rlist">
                    {allModels.map(o => {
                      const on = cfg.agentModels.includes(o.rowId);
                      return (
                        <button key={o.rowId} type="button" className="lt3-opt" data-on={on} onClick={() => togglePoolModel(o.rowId)}>
                          <span className="lt3-check" data-on={on}>{on && <Ic.Check size={11} />}</span>
                          <span className="lt3-opt-m"><span className="lt3-opt-t">{o.modelId}</span><span className="lt3-opt-d">{o.provider} · {o.credentialName}</span></span>
                        </button>
                      );
                    })}
                    {allModels.length === 0 && <div className="lt3-rempty">No credentialed models.</div>}
                  </div>
                </RowPop>
                <RowPop label="Agent pool" value={agentPoolLabel}>
                  <div className="lt3-poolhint">The supervisor only spawns these agents. Leave empty to allow any suitable agent.</div>
                  <div className="lt3-rlist">
                    {allPersonas.map(p => {
                      const on = cfg.agentPool.includes(p.id);
                      return (
                        <button key={p.id} type="button" className="lt3-opt" data-on={on} onClick={() => togglePoolAgent(p.id)}>
                          <span className="lt3-check" data-on={on}>{on && <Ic.Check size={11} />}</span>
                          <span className="lt3-opt-m"><span className="lt3-opt-t">{p.name}</span></span>
                        </button>
                      );
                    })}
                    {allPersonas.length === 0 && <div className="lt3-rempty">No agent definitions.</div>}
                  </div>
                </RowPop>
                <RowPop label="Concurrency" value={`${cfg.maxParallel} agents at once`}>
                  <div className="lt3-limits">
                    <input value={cfg.maxParallel} onChange={e => setC({ maxParallel: e.target.value })} aria-label="Max agents at once" /><span>agents at once</span>
                  </div>
                  <div className="lt3-poolhint">The run keeps working the plan until it's done — bounded by this concurrency and the Budget, not a fixed round or agent count.</div>
                </RowPop>
                <Combo label="Autonomy ceiling" value={cfg.autonomyCeiling} options={[{ value: "", label: "Inherit" }, ...reachablePerms(effort).map(p => ({ value: p.v, label: p.v }))]} onChange={v => setC({ autonomyCeiling: v })} />
                <div className="lt3-poolhint">A cap on what the run's agents may reach, applied on top of the tier's own ceiling. It can only TIGHTEN it (EffortRouter.TightenCeiling), so setting Standard here forbids network even when the Permissions tab asked for it. Unleashed isn't offered — no preset reaches it.</div>
                <Combo label="Integrate branches" value={cfg.integrateBranches} options={BOOLEAN_OVERRIDE_OPTIONS} onChange={v => setC({ integrateBranches: v as LaunchBooleanOverride })} />
                <div className="lt3-hrow">
                  <Combo label="Decision critic" value={cfg.decisionReview} options={[
                    { value: "None", label: "Off", desc: "Decisions execute unreviewed — plan decisions can still use the Plan critic" },
                    { value: "Gate", label: "Gate — block a weak decision", desc: "Hard gate: a flagged decision does not execute — self-revise, re-review, then escalate to you" },
                    { value: "Improve", label: "Improve — revise once against the critique", desc: "One self-revision folds the critique in, then the decision executes" },
                  ]} onChange={v => setC({ decisionReview: v })} />
                  <HelpTip title="Decision critic — what each option does" note="Your current choice is the vivid lane. Reviews every supervisor move BEFORE it takes effect; your approve on an escalation absolves only the very next decision.">
                    <DecisionLadderDiagram current={cfg.decisionReview} />
                  </HelpTip>
                </div>
              </> : (
                <div className="lt3-cdisabled">Coordination runs in <b>Deep</b> mode. Switch Effort to Deep to configure how multiple agents coordinate, review, and retry.</div>
              )}
              </>}

              {customizeTab === "safety" && <>
                <div className="lt3-cnote">What agents can do alone, and when they must ask.</div>
                <Combo label="Permissions" value={autonomyShown} options={reachablePerms(effort, cfg.autonomyCeiling).map(p => ({ value: p.v, label: p.v, desc: p.d }))} onChange={setAutonomy} />
                {!tierGrantsNetwork(effort)
                  ? <TierRow label="Network access" tier="Off — only Standard and Deep can grant it" />
                  : ceilingShown !== "Trusted"
                    // The Coordination tab's own ceiling already forbids it. Arming the switch here would let the
                    // operator pick On and watch it snap back — name what forbids it instead (the same doctrine
                    // as the off-tier row above).
                    ? <TierRow label="Network access" tier={`Off — the Coordination tab's ${ceilingShown} ceiling forbids it`} />
                    : <Combo label="Network access" value={networkOn ? "on" : "off"} options={NETWORK_OPTIONS} onChange={v => setAutonomy(v === "on" ? "Trusted" : "Standard")} />}
                <div className="lt3-poolhint" data-testid="network-posture">{networkPosture}</div>
                <SToggleRow label="Ask when uncertain" on locked />
                <SToggleRow label="Approve irreversible actions" on locked />
                <SToggleRow label="Stop before merge / push" on locked />
                <Combo label="Time limit" value={effectiveTimeLimit} options={timeLimitOpts} onChange={v => { setTimeLimitTouched(true); setC({ timeLimit: v }); }} />
              </>}

              {customizeTab === "planning" && <>
                <div className="lt3-cnote">Think it through before any agent runs. Confirm-plan-first parks every plan for your approval (any answer that isn't "approve" becomes revision feedback). The plan critic reviews the PLAN itself on every tier; the reviewer model serves ALL critics (plan / decision / output).</div>
                {planCapable ? <SToggleRow label="Confirm plan first" on={cfg.requirePlanConfirmation} onToggle={() => setC({ requirePlanConfirmation: !cfg.requirePlanConfirmation })} /> : <TierRow label="Confirm plan first" tier="Quick runs without a plan" />}
                {planCapable
                  ? <div className="lt3-hrow">
                      <Combo label="Plan critic" value={cfg.plannerReview} options={planCriticOpts} onChange={v => setC({ plannerReview: v })} />
                      <HelpTip title="Plan critic — what each option does" note="Your current choice is the vivid lane. Reviewer model / Independent agent picks WHO reviews. On Deep, Gate is the hard ladder — a flagged plan does not execute.">
                        <PlanCriticDiagram current={cfg.plannerReview} />
                      </HelpTip>
                    </div>
                  : <TierRow label="Plan critic" tier="Quick runs without a plan" />}
                <Combo label="Reviewer model" value={cfg.reviewerModel} options={[{ value: "", label: "Auto · independent", desc: "Prefers a different model than the producer; a one-model pool falls back to the same model, independently prompted" }, ...allModels.map(o => ({ value: o.rowId, label: o.modelId, desc: modelDesc(o) }))]} onChange={v => setC({ reviewerModel: v })} searchable />
              </>}

              {customizeTab === "evaluation" && <>
                <div className="lt3-cnote">How the result is judged. Criteria STEER on every tier — Deep renders them into the supervisor, Standard into the planner (the plan's per-item contracts target them), Quick into the agent's goal. Checks VERIFY — a command that must exit 0, or the result fails: Deep at the terminal stop, Quick against the produced branch; Standard verifies per item via the plan's own contracts.</div>
                {effort === "standard" && <TierRow label="Acceptance checks" tier="Per item — the plan authors each subtask's check" />}
                <RowPop label="Acceptance criteria" value={cfg.acceptance.length ? cfg.acceptance.join(" · ") : "None"}>
                  <div className="lt3-chips2">
                    {cfg.acceptance.map((v, i) => <span key={i} className="lt3-chip2">{v}<button type="button" onClick={() => setC({ acceptance: cfg.acceptance.filter((_, idx) => idx !== i) })}><Ic.X size={11} /></button></span>)}
                    <input className="lt3-chip2-add" placeholder="+ add" value={acceptDraft} onChange={e => setAcceptDraft(e.target.value)}
                      onKeyDown={e => {
                        if (e.key !== "Enter") return;
                        e.preventDefault();
                        const v = acceptDraft.trim();
                        if (v && !cfg.acceptance.includes(v)) setC({ acceptance: [...cfg.acceptance, v] });
                        setAcceptDraft("");
                      }} />
                  </div>
                </RowPop>
                {effort !== "standard" && <RowPop label="Acceptance checks" value={cfg.acceptanceChecks.length ? cfg.acceptanceChecks.join(" ") : (tier !== "Prototype" ? "Required — add a check" : "None")}>
                  <div className="lt3-chips2">
                    {cfg.acceptanceChecks.map((v, i) => <span key={i} className="lt3-chip2">{v}<button type="button" onClick={() => setC({ acceptanceChecks: cfg.acceptanceChecks.filter((_, idx) => idx !== i) })}><Ic.X size={11} /></button></span>)}
                    <input className="lt3-chip2-add" placeholder="+ command, e.g. sh check.sh" value={checksDraft} onChange={e => setChecksDraft(e.target.value)}
                      onKeyDown={e => {
                        if (e.key !== "Enter") return;
                        e.preventDefault();
                        // Split on whitespace: the backend execs a pure argv (no shell), so a pasted "sh check.sh"
                        // must become two tokens — one space-containing chip would ENOENT the whole floor at stop.
                        // No dedupe (unlike criteria): argv is a SEQUENCE and repeated tokens are legitimate.
                        const parts = checksDraft.trim().split(/\s+/).filter(Boolean);
                        if (parts.length) setC({ acceptanceChecks: [...cfg.acceptanceChecks, ...parts] });
                        setChecksDraft("");
                      }} />
                  </div>
                </RowPop>}
                <div className="lt3-hrow">
                  <Combo label="Agent output critic" value={cfg.outputReview} options={[
                    { value: "None", label: "Off", desc: "No result review — acceptance checks (if any) still verify objectively" },
                    { value: "Gate", label: "Gate — flag a weak change for human review", desc: "Pick when you review results: a weak change lands NeedsReview for you, never silently consumed" },
                    { value: "Improve", label: "Improve — feed the critique back, agent revises", desc: "Pick for unattended runs: the critique feeds back and buys a self-revise round" },
                  ]} onChange={v => setC({ outputReview: v })} />
                  <HelpTip title="Output critic — what each option does" note="Your current choice is the vivid lane. Reviewer = WHO judges (model reads the diff · real agent clones the repo). Self-revise = the ×N. Checks run first, objectively; criteria are the critic's yardstick.">
                    <EvaluationPipelineDiagram current={cfg.outputReview} />
                  </HelpTip>
                </div>
                <Combo label="Reviewer" value={cfg.reviewerAgent ? "agent" : "model"} options={[
                  { value: "model", label: "Model — in-process critic reads the diff", desc: "Fast and cheap — judges the change as text" },
                  effort === "deep"
                    ? { value: "agent", label: "Independent agent — reviews the plan, other harness", desc: "Strongest for the plan: an independent run inspects the repo before it executes. Spawned agents still get the in-process model critic only — Deep has no per-unit independent-agent reviewer" }
                    : { value: "agent", label: "Independent agent — clones the branch, other harness", desc: "Strongest — a real run inspects the actual repo (plans too); its approval is co-signed by a model" },
                ]} onChange={v => setC({ reviewerAgent: v === "agent" })} />
                {effort === "deep"
                  ? <TierRow label="Self-revise" tier="Deep units revise via the supervisor's retry loop" />
                  : <Combo label="Self-revise" value={cfg.reviseRounds} options={[{ value: "", label: "Auto — one round under Improve" }, { value: "0", label: "Off — a failure stands immediately" }, { value: "1", label: "1 round — feed the failure back once" }, { value: "2", label: "2 rounds" }]} onChange={v => setC({ reviseRounds: v })} />}
              </>}
            </div>
          </div>
        )}

        {launch.isError && <div className="lt3-err">{(launch.error as Error)?.message ?? "Launch failed"}</div>}
    </>
  );

  if (inline) return <div className="lt3 lt3-inline">{content}</div>;

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl lt3" role="dialog" aria-modal="true">{content}</div>
    </>,
    document.body,
  );
}

function SendGlyph() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M12 19V5" /><path d="M5 12l7-7 7 7" />
    </svg>
  );
}

/** A settings row whose value opens a custom popover (Limits, Acceptance). */
/** An off-tier control rendered honestly: a muted read-only row naming the tier that owns it — never an armed switch the wire would silently drop (the same doctrine as the locked safety rows). */
function TierRow({ label, tier }: { label: string; tier: string }) {
  return (
    <div className="lt3-srow lt3-srow-ro">
      <span className="lt3-srow-l">{label}</span>
      <span className="lt3-combo-v">{tier}</span>
    </div>
  );
}

function RowPop({ label, value, children }: { label: string; value: string; children: ReactNode }) {
  const { open, setOpen, btnRef, popRef, pos } = usePopover();
  return (
    <>
      <button ref={btnRef} type="button" className="lt3-srow" data-open={open} onClick={() => setOpen(v => !v)}>
        <span className="lt3-srow-l">{label}</span>
        <span className="lt3-combo-v">{value}</span>
        <Ic.ChevronRight size={15} />
      </button>
      {open && pos && createPortal(
        <div ref={popRef} className="lt3-pop lt3-rowpop" style={{ position: "fixed", left: pos.left, top: pos.top, minWidth: Math.max(pos.width, 260) }}>
          {children}
        </div>,
        document.body,
      )}
    </>
  );
}

/** Branch picker — searchable Combo fed by the repo's branches. Compact label in the repo row. */
function BranchCombo({ repoId, value, onChange }: { repoId: string; value: string; onChange: (b: string) => void }) {
  const branches = useRepositoryBranches(repoId || null);
  const opts: Option[] = [{ value: "", label: "default" }, ...(branches.data ?? []).map(b => ({ value: b.name, label: b.name }))];
  return <Combo value={value} options={opts} onChange={onChange} placeholder="default" searchable buttonClassName="lt3-branch-btn" />;
}

function Pop({ align, wide, children }: { align: "left" | "right"; wide?: boolean; children: ReactNode }) {
  return <div className={`lt3-pop${wide ? " lt3-pop-wide" : ""}`} data-align={align}>{children}</div>;
}

/** Settings toggle row: label · On/Off · switch. */
function SToggleRow({ label, on, onToggle, locked }: { label: string; on: boolean; onToggle?: () => void; locked?: boolean }) {
  // A `locked` row is an HONEST display of an always-enforced safety floor (the irreversible-HITL gate + the decision
  // substrate) — not a real toggle. Showing it as a switch would be a lie: there is no per-run way to turn these off,
  // so we render a non-interactive "Always on" indicator instead of a fake switch.
  if (locked) {
    return (
      <div className="lt3-srow lt3-srow-ro">
        <span className="lt3-srow-l">{label}</span>
        <span className="lt3-combo-v">Always on</span>
      </div>
    );
  }
  return (
    <button type="button" className="lt3-srow" onClick={onToggle} aria-pressed={on}>
      <span className="lt3-srow-l">{label}</span>
      <span className="lt3-tog" data-on={on}><span /></span>
    </button>
  );
}

/** Title-case an open effort string for display — the same transform the backend applies when it builds an
 *  option label, used only where no backend label exists (the hint has no confirm card, so no options). */
const titleCase = (v: string) => (v ? v[0].toUpperCase() + v.slice(1) : v);

/**
 * B1 — the ROUTE CONFIRM card, above the composer box. The router builds this whenever an auto route landed
 * below its confidence floor OR the classifier flagged risky side effects; until now nothing rendered it and the
 * run started anyway. It blocks the Launch button, and the only way to answer it is to pick a tier — which
 * leaves as an EXPLICIT effort, short-circuiting the classifier so the second route is deterministic.
 *
 * <p>The options come from `confirm.options` (derived server-side from the live bounds presets), never a
 * hardcoded list — a new tier appears here with no frontend edit.</p>
 */
function RouteConfirmCard({ route, onPick }: { route: RoutePlan; onPick: (mode: string) => void }) {
  const confirm = route.confirm;
  if (!confirm) return null;

  const risky = route.decision?.signals?.riskySideEffects === true;
  // The BACKEND owns tier copy: every label here comes from the option the router emitted, so a renamed or new
  // preset reads correctly with no frontend edit. Only a suggested mode with no matching option falls back.
  const suggestedLabel = confirm.options.find(o => o.mode === confirm.suggestedMode)?.label ?? titleCase(confirm.suggestedMode);

  return (
    <div className="lt3-route" data-risk={risky} data-testid="route-confirm-card">
      <div className="lt3-route-h">
        {risky ? <Ic.Triangle size={14} /> : <Ic.Compass size={14} />}
        <span>{risky ? "This looks irreversible — confirm the depth" : "Confirm the depth before launching"}</span>
        {risky && <span className="lt3-route-badge" data-testid="route-risk-badge">Risky side effects</span>}
      </div>

      <div className="lt3-route-why">
        <b>{suggestedLabel}</b> suggested · {confirm.rationale}
      </div>

      {route.degradedReason && <div className="lt3-route-degraded">{route.degradedReason}</div>}

      <div className="lt3-route-opts" role="group" aria-label="Confirm the effort">
        {confirm.options.map(o => (
          <button
            key={o.mode}
            type="button"
            className="lt3-route-opt"
            data-suggested={o.mode === confirm.suggestedMode}
            onClick={() => onPick(o.mode)}
          >
            <span className="lt3-route-opt-t">{o.label || titleCase(o.mode)}</span>
            {o.hint && <span className="lt3-route-opt-d">{o.hint}</span>}
          </button>
        ))}
      </div>

      <div className="lt3-route-f">Picking a depth launches at that tier explicitly — the classifier is not consulted again.</div>
    </div>
  );
}

/** B1 — the quiet route hint for an auto launch the router was confident about: one line naming where it goes and
 *  why, so "Auto" is never an unexplained black box even when there is nothing to confirm. There is no confirm
 *  card here and therefore no backend-authored label, so the tier renders as the router's own open string. */
function RouteHint({ route }: { route: RoutePlan }) {
  const why = route.degradedReason || route.decision?.rationale || "";

  return (
    <div className="lt3-route-quiet" data-testid="route-hint">
      Auto → <b>{titleCase(route.effortMode)}</b>{why && <> · {why}</>}
    </div>
  );
}

/** P5-7 — the spec-preview suggestion card: editable PROPOSALS between the box and Customize. Applying writes
 *  the SAME cfg fields the Evaluation tab edits (no parallel state); dismissing is keyed to the suggestion's
 *  content upstream, so it leaves no trace; a null suggestion never mounts this at all. Checks hide on
 *  Standard (that tier verifies per plan item and never sends the argv floor — an Apply there would be a lie).
 *  When the model suggested NO check, that absence is shown as its own row (the most decision-relevant fact on
 *  the card) and the model's note below carries its own why. */
function SpecSuggestionCard({ suggestion, grounded, applied, checksApplicable, onApplyChecks, onApplyCriteria, onDismiss }: {
  suggestion: TaskSpecSuggestion;
  grounded: boolean;
  applied: { checks: boolean; criteria: boolean };
  checksApplicable: boolean;
  onApplyChecks: () => void;
  onApplyCriteria: () => void;
  onDismiss: () => void;
}) {
  const hasChecks = checksApplicable && suggestion.acceptanceChecks.length > 0;
  const noChecks = checksApplicable && suggestion.acceptanceChecks.length === 0;
  const hasCriteria = suggestion.acceptanceCriteria.length > 0;
  const allApplied = (!hasChecks || applied.checks) && (!hasCriteria || applied.criteria);
  const band = suggestion.confidence >= 0.75 ? "high" : suggestion.confidence >= 0.5 ? "mid" : "low";
  if (!hasChecks && !hasCriteria) return null;
  return (
    <div className="lt3-spec" data-testid="spec-suggestion-card">
      <div className="lt3-spec-h">
        <Ic.Sparkles size={14} />
        <span>Suggested contract</span>
        <span className="lt3-spec-badge" data-band={band}>{Math.round(suggestion.confidence * 100)}% confident</span>
        {grounded
          ? <span className="lt3-spec-badge">Grounded in repo layout</span>
          : <span className="lt3-spec-badge" data-warn="true">Repo not read — verify the check</span>}
        <button type="button" className="lt3-spec-x" aria-label="Dismiss suggestion" onClick={onDismiss}><Ic.X size={13} /></button>
      </div>

      {hasChecks && (
        <div className="lt3-spec-row">
          <span className="lt3-spec-l">Checks</span>
          <span className="lt3-spec-v">
            <span className="lt3-spec-cmd">{suggestion.acceptanceChecks.map((t, i) => <code key={i} className="lt3-spec-chip">{t}</code>)}</span>
            <span className="lt3-spec-sub">Runs after the work — exit 0 or the result fails{applied.checks && <span className="lt3-spec-went"> · filled into Evaluation → Acceptance checks</span>}</span>
          </span>
          <button type="button" className="lt3-spec-apply" disabled={applied.checks} title="Fills Evaluation → Acceptance checks (editable there)" onClick={onApplyChecks}>{applied.checks ? <><Ic.Check size={11} /> Applied</> : "Apply"}</button>
        </div>
      )}
      {noChecks && (
        <div className="lt3-spec-row">
          <span className="lt3-spec-l">Checks</span>
          <span className="lt3-spec-v lt3-spec-none">None suggested — the model's note below says why. Add your own under Evaluation if you know the command.</span>
        </div>
      )}
      {hasCriteria && (
        <div className="lt3-spec-row">
          <span className="lt3-spec-l">Criteria</span>
          <span className="lt3-spec-v">
            <ul className="lt3-spec-list">{suggestion.acceptanceCriteria.map((crit, i) => <li key={i}>{crit}</li>)}</ul>
            <span className="lt3-spec-sub">Steers the work — rendered into the agent's brief{applied.criteria && <span className="lt3-spec-went"> · filled into Evaluation → Acceptance criteria</span>}</span>
          </span>
          <button type="button" className="lt3-spec-apply" disabled={applied.criteria} title="Fills Evaluation → Acceptance criteria (editable there)" onClick={onApplyCriteria}>{applied.criteria ? <><Ic.Check size={11} /> Applied</> : "Apply"}</button>
        </div>
      )}

      {suggestion.rationale && <div className="lt3-spec-note">{suggestion.rationale}</div>}

      <div className="lt3-spec-f">
        <span className="lt3-spec-r">Kept suggestions launch as your own fields · dismissing leaves no trace</span>
        <button type="button" className="lt3-spec-all" disabled={allApplied} onClick={() => { if (hasChecks) onApplyChecks(); if (hasCriteria) onApplyCriteria(); }}>Apply all</button>
      </div>
    </div>
  );
}
