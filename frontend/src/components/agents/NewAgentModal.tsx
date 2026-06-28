import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useInstantiateAgentFromStore } from "@/hooks/use-agents";
import { usePack, usePacks } from "@/hooks/use-packs";
import { agentArtifacts, packsWithAgents } from "./newAgentPicker";

interface NewAgentModalProps {
  /** Chose "Custom" — the parent closes this and opens the blank authoring editor. */
  onCustom: () => void;
  /** A Library agent was instantiated — the parent opens the new working copy for renaming/tweaking. */
  onCreated: (id: string) => void;
  onClose: () => void;
}

type Step = "choose" | "library";

/**
 * "New agent" dialog, mirroring the Add-workflow chooser. Step 1 offers two on-ramps: Custom (author a blank
 * persona — the parent opens the existing editor unchanged) or From Library (copy a store snapshot). The Library
 * path drills packs → agent artifacts (search-filtered, since a pack can hold hundreds), and picking one calls the
 * instantiate endpoint and hands the new working-copy id back so the parent can open it. Warm-theme `.mdl` portal.
 */
export function NewAgentModal({ onCustom, onCreated, onClose }: NewAgentModalProps) {
  const [step, setStep] = useState<Step>("choose");
  const [packId, setPackId] = useState<string | null>(null);
  const [query, setQuery] = useState("");

  const packs = usePacks();
  const pack = usePack(packId);
  const instantiate = useInstantiateAgentFromStore();
  const pending = instantiate.isPending;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !pending) onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, pending]);

  function back() {
    instantiate.reset();   // drop any failed-attempt banner so it doesn't follow us into another pack
    if (packId) { setPackId(null); setQuery(""); }
    else setStep("choose");
  }

  async function pick(sourceDefinitionId: string) {
    if (pending) return;
    try {
      const { id } = await instantiate.mutateAsync(sourceDefinitionId);
      onCreated(id);
    } catch {
      /* failure surfaced via the instantiate.isError banner below */
    }
  }

  const sub = step === "choose"
    ? "Start from scratch, or copy one from your Library."
    : packId ? "Pick an agent to copy into a new working agent." : "Choose a Library pack.";

  const eligiblePacks = packsWithAgents(packs.data ?? []);
  // Trust the detail ONLY when it's for the SELECTED pack — usePack keeps the previous pack's data as a placeholder
  // during a switch (isLoading stays false), which would otherwise let a fast click instantiate the wrong pack's agent.
  const packReady = !!packId && pack.data?.pack.id === packId;
  const agents = packReady ? agentArtifacts(pack.data!.artifacts, query) : [];

  return createPortal(
    <>
      <div className="mdl-mask" onClick={() => { if (!pending) onClose(); }} />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          {step !== "choose" && (
            <button type="button" className="mdl-back" onClick={back} title="Back"><Ic.ChevronLeft size={16} /></button>
          )}
          <div className="mdl-title-wrap">
            <div className="mdl-title">New agent</div>
            <div className="mdl-sub">{sub}</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          {step === "choose" && (
            <div className="wf-add-choices">
              <button type="button" className="wf-add-choice" onClick={onCustom}>
                <span className="wf-add-choice-ic"><Ic.Plus size={20} /></span>
                <span className="wf-add-choice-name">Custom</span>
                <span className="wf-add-choice-desc">Author a new agent from scratch.</span>
              </button>
              <button type="button" className="wf-add-choice" onClick={() => setStep("library")}>
                <span className="wf-add-choice-ic"><Ic.Box size={20} /></span>
                <span className="wf-add-choice-name">From Library</span>
                <span className="wf-add-choice-desc">Copy an agent from an imported pack.</span>
                <span className="wf-add-choice-arrow"><Ic.ChevronRight size={16} /></span>
              </button>
            </div>
          )}

          {step === "library" && !packId && (
            packs.isLoading ? <div className="wf-form-help">Loading your Library…</div>
            : packs.isError ? <div className="wf-form-help">Couldn't load your Library — try again.</div>
            : eligiblePacks.length === 0 ? <div className="wf-form-help">No agents in your Library yet — import a pack first.</div>
            : (
              <div className="wf-add-choices">
                {eligiblePacks.map((p) => (
                  <button type="button" key={p.id} className="wf-add-choice" onClick={() => setPackId(p.id)}>
                    <span className="wf-add-choice-ic"><Ic.Box size={20} /></span>
                    <span className="wf-add-choice-name">{p.name}</span>
                    <span className="wf-add-choice-desc">{p.agentCount} {p.agentCount === 1 ? "agent" : "agents"}</span>
                    <span className="wf-add-choice-arrow"><Ic.ChevronRight size={16} /></span>
                  </button>
                ))}
              </div>
            )
          )}

          {step === "library" && packId && (
            <>
              <div className="imp-search" style={{ maxWidth: "none", marginBottom: 12 }}>
                <Ic.Search size={14} />
                <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search agents…" aria-label="Search agents" autoFocus />
              </div>
              {!packReady ? <div className="wf-form-help">Loading…</div>
              : agents.length === 0 ? <div className="wf-form-help">No agent matches your search.</div>
              : (
                <div className="wf-add-choices">
                  {agents.map((a) => (
                    <button type="button" key={a.id} className="wf-add-choice" disabled={pending} onClick={() => pick(a.id)}>
                      <span className="wf-add-choice-ic"><Ic.Bot size={20} /></span>
                      <span className="wf-add-choice-name">{a.name}</span>
                      <span className="wf-add-choice-desc">{a.description ?? `@${a.slug}`}</span>
                      <span className="wf-add-choice-arrow"><Ic.Plus size={16} /></span>
                    </button>
                  ))}
                </div>
              )}
              {instantiate.isError && <div className="wf-form-help" style={{ color: "var(--danger)" }}>Couldn't add that agent — try again.</div>}
            </>
          )}
        </div>
      </div>
    </>,
    document.body,
  );
}
