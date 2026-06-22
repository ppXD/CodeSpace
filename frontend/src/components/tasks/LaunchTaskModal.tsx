import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import type { LaunchTaskInput, TaskSurfaceKind } from "@/api/tasks";
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
  linkedEntity?: { label: string; url?: string };
}

export interface LaunchTaskModalProps {
  surface: TaskSurfaceKind;
  autofill?: LaunchTaskAutofill;
  onClose: () => void;
  /** Receives the started run's id so the caller can navigate to its phase tree. */
  onLaunched?: (runId: string) => void;
}

interface WorkspaceRepo { repositoryId: string; branch: string; access: "write" | "read"; alias: string; isPrimary: boolean }
interface Option { value: string; label: string; desc?: string }

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
export function LaunchTaskModal({ surface, autofill, onClose, onLaunched }: LaunchTaskModalProps) {
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
  const [agentDefinitionId, setAgentDefinitionId] = useState("");
  const [runnerKind, setRunnerKind] = useState("");
  const [expanded, setExpanded] = useState(false);
  const [menu, setMenu] = useState<null | "perm" | "repos" | "mr">(null);
  const [effortOpen, setEffortOpen] = useState(false);
  const [repoSearch, setRepoSearch] = useState("");
  const [customizeTab, setCustomizeTab] = useState<"execution" | "supervisor" | "safety">("execution");
  // Design-ahead Customize config (interactive UI state; not yet sent to the launch command).
  const [cfg, setCfg] = useState({
    branchMode: "auto", tools: "default", enableMcp: true, cwdMode: "auto",
    agentModels: [] as string[], agentPool: [] as string[],
    maxParallel: "5", maxRounds: "6", maxAgents: "20", budget: "none",
    integrateBranches: false, autonomyCeiling: "",
    acceptance: ["tests pass", "PR opened"] as string[],
    askWhenUncertain: true, requireApproval: true, stopBeforeMerge: true,
    decisionSurface: "run-activity", timeout: "safe-default", notifyChat: "off",
  });
  const setC = (p: Partial<typeof cfg>) => setCfg(c => ({ ...c, ...p }));
  const resetTab = () => {
    if (customizeTab === "execution") { setAgentDefinitionId(""); setHarness(""); setModel(""); setModelCredentialId(""); setRunnerKind(""); setC({ branchMode: "auto", tools: "default", enableMcp: true, cwdMode: "auto" }); }
    else if (customizeTab === "supervisor") setC({ agentModels: [], agentPool: [], maxParallel: "5", maxRounds: "6", maxAgents: "20", budget: "none", integrateBranches: false, autonomyCeiling: "", acceptance: ["tests pass", "PR opened"] });
    else setC({ askWhenUncertain: true, requireApproval: true, stopBeforeMerge: true, decisionSurface: "run-activity", timeout: "safe-default", notifyChat: "off" });
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
  const placeholder = effort === "deep" ? "Describe a goal to coordinate…" : "Describe a task…";

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
  if (!primary?.repositoryId) missing.push("a repository");
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
    const input: LaunchTaskInput = {
      taskText: taskText.trim(),
      surfaceKind: surface,
      repositoryId: primary?.repositoryId || null,
      baseBranch: primary?.branch || null,
      effort,
      autonomy,
      model: model || null,
      harness: harness || null,
      agentDefinitionId: agentDefinitionId || null,
      runnerKind: runnerKind || null,
      modelCredentialId: modelCredentialId || null,
    };
    launch.mutate(input, { onSuccess: res => onLaunched?.(res.runId) });
  };

  const harnessOpts: Option[] = [{ value: "", label: "Auto" }, ...(harnesses.data ?? []).map(h => ({ value: h.kind, label: h.kind }))];
  const runnerOpts: Option[] = [{ value: "", label: "Local sandbox" }];
  const modelOpts: Option[] = [{ value: "", label: "Auto" }, ...(credModels.data ?? []).map(o => ({ value: o.modelId, label: o.modelId, desc: `${o.provider} · ${o.credentialName}` }))];

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
  const poolModels = cfg.agentModels.length ? allModels.filter(o => cfg.agentModels.includes(o.modelId)) : allModels;
  const agentModelOpts: Option[] = [{ value: "", label: "Auto" }, ...poolModels.map(o => ({ value: o.modelId, label: o.modelId, desc: `${o.provider} · ${o.credentialName}` }))];
  const menuModels = effort === "deep" ? allModels : poolModels;
  const poolLabel = cfg.agentModels.length ? `${cfg.agentModels.length} model${cfg.agentModels.length > 1 ? "s" : ""}` : "All eligible models";
  // Toggle a model in the pool. Outside Deep the primary model IS the agent model, so if narrowing the
  // pool strands it, fall back to Auto. (In Deep the model is the unconstrained supervisor brain.)
  const togglePoolModel = (id: string) => {
    const next = cfg.agentModels.includes(id) ? cfg.agentModels.filter(m => m !== id) : [...cfg.agentModels, id];
    setC({ agentModels: next });
    if (effort !== "deep" && model && next.length && !next.includes(model)) { setModel(""); setModelCredentialId(""); }
  };
  const branchOpts: Option[] = [
    { value: "auto", label: "Create branch when changes exist" },
    { value: "always", label: "Always create a branch" },
    { value: "none", label: "Work in place" },
  ];
  const pickModel = (v: string) => { setModel(v); setModelCredentialId(credModels.data?.find(o => o.modelId === v)?.credentialId ?? ""); };

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl lt3" role="dialog" aria-modal="true">
        <div className="lt3-box">
          <textarea className="lt3-input" rows={3} placeholder={placeholder} value={taskText} onChange={e => setTaskText(e.target.value)} autoFocus />

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
              <button type="button" className="lt3-ctab" data-on={customizeTab === "execution"} onClick={() => setCustomizeTab("execution")}>Agent setup</button>
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
                <Combo label="Tools" value={cfg.tools} options={[{ value: "default", label: "Default" }]} onChange={v => setC({ tools: v })} />
                <Combo label="Branch" value={cfg.branchMode} options={branchOpts} onChange={v => setC({ branchMode: v })} />
                <Combo label="Working dir" value={cfg.cwdMode} options={[{ value: "auto", label: "Auto" }, { value: "workspace", label: "Workspace root" }, { value: "primary", label: "Primary repo" }]} onChange={v => setC({ cwdMode: v })} />
                <SToggleRow label="MCP tools" on={cfg.enableMcp} onToggle={() => setC({ enableMcp: !cfg.enableMcp })} />
              </>}

              {customizeTab === "supervisor" && <>
                <div className="lt3-cnote">How Deep mode plans, delegates, reviews, and stops.</div>
                {effort === "deep" || effort === "auto" ? <>
                <Combo label="Brain model" value={model} options={effort === "deep" ? modelOpts : agentModelOpts} onChange={pickModel} searchable />
                <RowPop label="Agent model pool" value={poolLabel}>
                  <div className="lt3-poolhint">Agents draw only from these models. Leave empty to allow all eligible models.</div>
                  <div className="lt3-rlist">
                    {allModels.map(o => {
                      const on = cfg.agentModels.includes(o.modelId);
                      return (
                        <button key={`${o.credentialId}/${o.modelId}`} type="button" className="lt3-opt" data-on={on} onClick={() => togglePoolModel(o.modelId)}>
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
                <RowPop label="Acceptance" value={cfg.acceptance.length ? cfg.acceptance.join(" · ") : "None"}>
                  <div className="lt3-chips2">
                    {cfg.acceptance.map((v, i) => <span key={i} className="lt3-chip2">{v}<button type="button" onClick={() => setC({ acceptance: cfg.acceptance.filter((_, idx) => idx !== i) })}><Ic.X size={11} /></button></span>)}
                    <span className="lt3-chip2-add">+ add</span>
                  </div>
                </RowPop>
                <Combo label="Autonomy ceiling" value={cfg.autonomyCeiling} options={[{ value: "", label: "Inherit" }, ...PERMS.map(p => ({ value: p.v, label: p.v }))]} onChange={v => setC({ autonomyCeiling: v })} />
                <SToggleRow label="Integrate branches" on={cfg.integrateBranches} onToggle={() => setC({ integrateBranches: !cfg.integrateBranches })} />
              </> : (
                <div className="lt3-cdisabled">Coordination runs in <b>Deep</b> mode. Switch Effort to Deep to configure how multiple agents coordinate, review, and retry.</div>
              )}
              </>}

              {customizeTab === "safety" && <>
                <div className="lt3-cnote">What agents can do alone, and when they must ask.</div>
                <Combo label="Permissions" value={autonomy} options={PERMS.map(p => ({ value: p.v, label: p.v, desc: p.d }))} onChange={setAutonomy} />
                <SToggleRow label="Ask when uncertain" on={cfg.askWhenUncertain} onToggle={() => setC({ askWhenUncertain: !cfg.askWhenUncertain })} />
                <SToggleRow label="Approve irreversible actions" on={cfg.requireApproval} onToggle={() => setC({ requireApproval: !cfg.requireApproval })} />
                <SToggleRow label="Stop before merge / push" on={cfg.stopBeforeMerge} onToggle={() => setC({ stopBeforeMerge: !cfg.stopBeforeMerge })} />
                <Combo label="Decision surface" value={cfg.decisionSurface} options={[{ value: "run-activity", label: "Run activity" }]} onChange={v => setC({ decisionSurface: v })} />
                <Combo label="Notify in chat" value={cfg.notifyChat} options={[{ value: "off", label: "Off" }, { value: "channel", label: "Current channel" }]} onChange={v => setC({ notifyChat: v })} />
                <Combo label="Timeout" value={cfg.timeout} options={[{ value: "safe-default", label: "Safe default" }, { value: "pause", label: "Pause and wait" }, { value: "reject", label: "Safe reject" }]} onChange={v => setC({ timeout: v })} />
              </>}
            </div>
          </div>
        )}

        {launch.isError && <div className="lt3-err">{(launch.error as Error)?.message ?? "Launch failed"}</div>}
      </div>
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

/** Shared popover machinery — portal'd to <body>, fixed-positioned from the trigger rect, dismissed on
 *  outside click or on scroll of the content behind it (not the popover's own list). */
function usePopover() {
  const [open, setOpen] = useState(false);
  const btnRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top: number; width: number } | null>(null);
  useLayoutEffect(() => {
    if (open && btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ left: r.left, top: r.bottom + 5, width: Math.max(r.width, 200) });
    }
  }, [open]);
  useEffect(() => {
    if (!open) return;
    const inside = (n: EventTarget | null) => btnRef.current?.contains(n as Node) || popRef.current?.contains(n as Node);
    const onDown = (e: MouseEvent) => { if (!inside(e.target)) setOpen(false); };
    const onScroll = (e: Event) => { if (!popRef.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", onDown);
    window.addEventListener("scroll", onScroll, true);
    return () => { document.removeEventListener("mousedown", onDown); window.removeEventListener("scroll", onScroll, true); };
  }, [open]);
  return { open, setOpen, btnRef, popRef, pos };
}

/** In-house warm dropdown. With `label`, renders as a compact settings ROW (label · value · ›); without,
 *  a boxed select / pill. Optional top search for long lists. */
function Combo({ label, value, options, onChange, placeholder, searchable, buttonClassName }: {
  label?: string;
  value: string;
  options: Option[];
  onChange: (v: string) => void;
  placeholder?: string;
  searchable?: boolean;
  buttonClassName?: string;
}) {
  const { open, setOpen, btnRef, popRef, pos } = usePopover();
  const [q, setQ] = useState("");
  const sel = options.find(o => o.value === value);
  const filtered = searchable && q.trim() ? options.filter(o => o.label.toLowerCase().includes(q.trim().toLowerCase())) : options;
  const isRow = label !== undefined;
  return (
    <>
      <button ref={btnRef} type="button" className={buttonClassName ?? (isRow ? "lt3-srow" : "lt3-combo-btn")} data-open={open} onClick={() => setOpen(v => !v)}>
        {isRow && <span className="lt3-srow-l">{label}</span>}
        <span className="lt3-combo-v">{sel?.label ?? placeholder ?? "Select"}</span>
        {isRow ? <Ic.ChevronRight size={15} /> : <Ic.ChevronDown size={14} />}
      </button>
      {open && pos && createPortal(
        <div ref={popRef} className="lt3-pop lt3-combo-pop" style={{ position: "fixed", left: pos.left, top: pos.top, minWidth: pos.width }}>
          {searchable && <input className="lt3-search" placeholder="Search" value={q} onChange={e => setQ(e.target.value)} autoFocus />}
          <div className="lt3-combo-list">
            {filtered.map(o => (
              <button key={o.value} type="button" className="lt3-opt" data-on={o.value === value} onClick={() => { onChange(o.value); setOpen(false); setQ(""); }}>
                <span className="lt3-opt-m"><span className="lt3-opt-t">{o.label}</span>{o.desc && <span className="lt3-opt-d">{o.desc}</span>}</span>
                {o.value === value && <Ic.Check size={14} />}
              </button>
            ))}
            {filtered.length === 0 && <div className="lt3-rempty">No matches</div>}
          </div>
        </div>,
        document.body,
      )}
    </>
  );
}

/** A settings row whose value opens a custom popover (Limits, Acceptance). */
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
function SToggleRow({ label, on, onToggle }: { label: string; on: boolean; onToggle: () => void }) {
  return (
    <button type="button" className="lt3-srow" onClick={onToggle} aria-pressed={on}>
      <span className="lt3-srow-l">{label}</span>
      <span className="lt3-tog" data-on={on}><span /></span>
    </button>
  );
}
