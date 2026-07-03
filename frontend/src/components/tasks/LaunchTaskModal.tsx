import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import type { TaskSurfaceKind } from "@/api/tasks";
import { buildLaunchInput, DEFAULT_ACCEPTANCE } from "@/lib/launchInput";
import { Combo, type Option } from "@/components/common/Combo";
import { usePopover } from "@/components/common/usePopover";
import { Ic } from "@/_imported/ai-code-space/icons";
import { useAgentDefinitions, useHarnesses } from "@/hooks/use-agents";
import { useCredentialedModels } from "@/hooks/use-model-credentials";
import { useRepositories, useRepositoryBranches } from "@/hooks/use-repositories";
import { useLaunchTask } from "@/hooks/use-tasks";

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
  const [repoSearch, setRepoSearch] = useState("");
  const [customizeTab, setCustomizeTab] = useState<"execution" | "planning" | "supervisor" | "safety" | "evaluation">("execution");
  const [acceptDraft, setAcceptDraft] = useState("");
  const [checksDraft, setChecksDraft] = useState("");

  // Per-row tier honesty (the Coordination tab's lt3-cdisabled pattern, at row grain — these two tabs mix tiers):
  // an off-tier control renders as a muted read-only row instead of an armed switch the wire would silently drop.
  const planCapable = effort !== "quick";   // every tier that authors a plan can park on it + critique it
  // Design-ahead Customize config (interactive UI state; not yet sent to the launch command).
  const [cfg, setCfg] = useState({
    pushBranch: false, tools: [] as string[], enableMcp: false, cwdMode: "auto",
    agentModels: [] as string[], agentPool: [] as string[],
    maxParallel: "5", maxRounds: "6", maxAgents: "20", budget: "none",
    integrateBranches: false, autonomyCeiling: "",
    acceptance: [...DEFAULT_ACCEPTANCE], acceptanceChecks: [] as string[],
    decisionSurface: "run-activity", timeout: "safe-default", timeLimit: "3600", notifyChat: "off",
    requirePlanConfirmation: false, plannerReview: "None",
    decisionReview: "None", outputReview: "None", reviewerModel: "", reviseRounds: "",
  });
  const setC = (p: Partial<typeof cfg>) => setCfg(c => ({ ...c, ...p }));
  const resetTab = () => {
    if (customizeTab === "execution") { setAgentDefinitionId(""); setHarness(""); setModel(""); setModelCredentialId(""); setRunnerKind(""); setC({ pushBranch: false, tools: [], enableMcp: false, cwdMode: "auto" }); }
    else if (customizeTab === "planning") setC({ requirePlanConfirmation: false, plannerReview: "None", reviewerModel: "" });
    else if (customizeTab === "supervisor") setC({ agentModels: [], agentPool: [], maxParallel: "5", maxRounds: "6", maxAgents: "20", budget: "none", integrateBranches: false, autonomyCeiling: "", decisionReview: "None" });
    else if (customizeTab === "evaluation") setC({ acceptance: [...DEFAULT_ACCEPTANCE], acceptanceChecks: [], outputReview: "None", reviseRounds: "" });
    else setC({ decisionSurface: "run-activity", timeout: "safe-default", timeLimit: "3600", notifyChat: "off" });
  };

  const repos = useRepositories();
  const harnesses = useHarnesses();
  const credModels = useCredentialedModels();
  const personas = useAgentDefinitions();
  const launch = useLaunchTask();

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
  const canLaunch = missing.length === 0 && !launch.isPending;

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
    // Resolve the picked (model, credential) to its concrete row id so the backend can pin the supervisor brain
    // (Deep) / the agent model (single-agent) by row, not guess between two credentials of the same model name.
    const modelCredentialModelId = credModels.data?.find(o => o.modelId === model && o.credentialId === modelCredentialId)?.rowId ?? "";
    const input = buildLaunchInput({
      taskText, surface, sessionId, workspace, effort, autonomy, model, modelCredentialId, modelCredentialModelId, harness, agentDefinitionId, runnerKind, cwdMode: cfg.cwdMode, enableMcp: cfg.enableMcp, tools: cfg.tools, pushBranch: cfg.pushBranch,
      maxParallel: cfg.maxParallel, maxRounds: cfg.maxRounds, maxAgents: cfg.maxAgents, budget: cfg.budget,
      agentModels: cfg.agentModels, agentPool: cfg.agentPool, autonomyCeiling: cfg.autonomyCeiling, timeLimit: cfg.timeLimit,
      integrateBranches: cfg.integrateBranches, acceptanceCriteria: cfg.acceptance, acceptanceChecks: cfg.acceptanceChecks,
      requirePlanConfirmation: cfg.requirePlanConfirmation, plannerReview: cfg.plannerReview,
      decisionReview: cfg.decisionReview, outputReview: cfg.outputReview, reviewerModel: cfg.reviewerModel, reviseRounds: cfg.reviseRounds,
    });
    launch.mutate(input, { onSuccess: res => onLaunched?.(res.runId) });
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
      <div className="lt3-box">
          <textarea ref={taRef} className="lt3-input" rows={inline ? 1 : 3} placeholder={placeholder} value={taskText} onChange={e => setTaskText(e.target.value)} autoFocus={!inline} />

          <div className="lt3-bar">
            <button type="button" className="lt3-pill lt3-adv" data-open={expanded} aria-expanded={expanded} title="Advanced settings — execution · supervisor · safety" onClick={() => setExpanded(v => !v)}>
              <Ic.Settings size={16} /><span>Advanced</span>
            </button>

            <div className="lt3-anchor">
              <button className="lt3-pill" title="Permission" onClick={() => setMenu(m => m === "perm" ? null : "perm")}>
                <Ic.Lock size={16} /><span>{autonomy}</span><Ic.ChevronDown size={14} />
              </button>
              {menu === "perm" && (
                <Pop align="left">
                  <div className="lt3-pop-t">Permission</div>
                  {PERMS.map(p => (
                    <button key={p.v} className="lt3-opt" data-on={autonomy === p.v} onClick={() => { setAutonomy(p.v); closeMenu(); }}>
                      <span className="lt3-opt-m"><span className="lt3-opt-t">{p.v}</span><span className="lt3-opt-d">{p.d}</span></span>
                      {autonomy === p.v && <Ic.Check size={14} />}
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

            <button className="lt3-send" aria-label="Launch task" disabled={!canLaunch} onClick={submit} title={canLaunch ? "Launch" : `Add ${missing.join(" and ")}`}>
              <SendGlyph />
            </button>
          </div>
        </div>

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
                <SToggleRow label="Publish branch" on={cfg.pushBranch} onToggle={() => setC({ pushBranch: !cfg.pushBranch })} />
                <Combo label="Working dir" value={cfg.cwdMode} options={[{ value: "auto", label: "Auto" }, { value: "workspace", label: "Workspace root" }, { value: "primary", label: "Primary repo" }]} onChange={v => setC({ cwdMode: v })} />
                <SToggleRow label="Force MCP fabric" on={cfg.enableMcp} onToggle={() => setC({ enableMcp: !cfg.enableMcp })} />
              </>}

              {customizeTab === "supervisor" && <>
                <div className="lt3-cnote">How Deep mode plans, delegates, reviews, and stops.</div>
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
                <RowPop label="Limits" value={`${cfg.maxParallel} parallel · ${cfg.maxRounds} rounds · ${cfg.maxAgents} agents`}>
                  <div className="lt3-limits">
                    <input value={cfg.maxParallel} onChange={e => setC({ maxParallel: e.target.value })} aria-label="Max parallel" /><span>parallel</span>
                    <input value={cfg.maxRounds} onChange={e => setC({ maxRounds: e.target.value })} aria-label="Max rounds" /><span>rounds</span>
                    <input value={cfg.maxAgents} onChange={e => setC({ maxAgents: e.target.value })} aria-label="Max agents" /><span>agents</span>
                  </div>
                </RowPop>
                <Combo label="Budget" value={cfg.budget} options={[{ value: "none", label: "No cap" }, { value: "5", label: "$5" }, { value: "10", label: "$10" }, { value: "25", label: "$25" }]} onChange={v => setC({ budget: v })} />
                <Combo label="Autonomy ceiling" value={cfg.autonomyCeiling} options={[{ value: "", label: "Inherit" }, ...PERMS.map(p => ({ value: p.v, label: p.v }))]} onChange={v => setC({ autonomyCeiling: v })} />
                <SToggleRow label="Integrate branches" on={cfg.integrateBranches} onToggle={() => setC({ integrateBranches: !cfg.integrateBranches })} />
                <Combo label="Decision critic" value={cfg.decisionReview} options={[{ value: "None", label: "Off" }, { value: "Gate", label: "Gate — flag a weak decision" }, { value: "Improve", label: "Improve — revise once against the critique" }]} onChange={v => setC({ decisionReview: v })} />
              </> : (
                <div className="lt3-cdisabled">Coordination runs in <b>Deep</b> mode. Switch Effort to Deep to configure how multiple agents coordinate, review, and retry.</div>
              )}
              </>}

              {customizeTab === "safety" && <>
                <div className="lt3-cnote">What agents can do alone, and when they must ask.</div>
                <Combo label="Permissions" value={autonomy} options={PERMS.map(p => ({ value: p.v, label: p.v, desc: p.d }))} onChange={setAutonomy} />
                <SToggleRow label="Ask when uncertain" on locked />
                <SToggleRow label="Approve irreversible actions" on locked />
                <SToggleRow label="Stop before merge / push" on locked />
                <Combo label="Decision surface" value={cfg.decisionSurface} options={[{ value: "run-activity", label: "Run activity" }]} onChange={v => setC({ decisionSurface: v })} />
                <Combo label="Notify in chat" value={cfg.notifyChat} options={[{ value: "off", label: "Off" }, { value: "channel", label: "Current channel" }]} onChange={v => setC({ notifyChat: v })} />
                <Combo label="Timeout" value={cfg.timeout} options={[{ value: "safe-default", label: "Safe default" }, { value: "pause", label: "Pause and wait" }, { value: "reject", label: "Safe reject" }]} onChange={v => setC({ timeout: v })} />
                <Combo label="Time limit" value={cfg.timeLimit} options={[{ value: "1800", label: "30 minutes" }, { value: "3600", label: "1 hour" }, { value: "7200", label: "2 hours" }, { value: "0", label: "No limit" }]} onChange={v => setC({ timeLimit: v })} />
              </>}

              {customizeTab === "planning" && <>
                <div className="lt3-cnote">Think it through before any agent runs. Confirm-plan-first parks every plan for your approval (any answer that isn't "approve" becomes revision feedback). The plan critic reviews the PLAN itself on every tier; the reviewer model serves ALL critics (plan / decision / output).</div>
                {planCapable ? <SToggleRow label="Confirm plan first" on={cfg.requirePlanConfirmation} onToggle={() => setC({ requirePlanConfirmation: !cfg.requirePlanConfirmation })} /> : <TierRow label="Confirm plan first" tier="Quick runs without a plan" />}
                {planCapable ? <Combo label="Plan critic" value={cfg.plannerReview} options={[{ value: "None", label: "Off" }, { value: "Gate", label: "Gate — annotate concerns onto the plan" }, { value: "Improve", label: "Improve — one revision against the critique" }]} onChange={v => setC({ plannerReview: v })} /> : <TierRow label="Plan critic" tier="Quick runs without a plan" />}
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
                {effort !== "standard" && <RowPop label="Acceptance checks" value={cfg.acceptanceChecks.length ? cfg.acceptanceChecks.join(" ") : "None"}>
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
                <Combo label="Agent output critic" value={cfg.outputReview} options={[{ value: "None", label: "Off" }, { value: "Gate", label: "Gate — flag a weak change for human review" }, { value: "Improve", label: "Improve — feed the critique back, agent revises" }]} onChange={v => setC({ outputReview: v })} />
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
