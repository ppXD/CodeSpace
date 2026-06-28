import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PackSummary } from "@/api/packs";
import { useInstantiateAgentFromStore } from "@/hooks/use-agents";
import { useDebounced } from "@/hooks/use-debounced";
import { useListPackArtifacts, usePacks } from "@/hooks/use-packs";

import { packsWithAgents } from "./newAgentPicker";

interface NewAgentModalProps {
  /** Chose "Custom" — the parent closes this and opens the blank authoring editor. */
  onCustom: () => void;
  /** A Library agent was instantiated — the parent opens the new working copy for renaming/tweaking. */
  onCreated: (id: string) => void;
  onClose: () => void;
}

type Step = "choose" | "library";

const AGENT_PAGE_SIZE = 10;

/**
 * "New agent" dialog, mirroring the Add-workflow chooser. Step 1 offers two on-ramps: Custom (author a blank
 * persona — the parent opens the existing editor unchanged) or From Library (copy a store snapshot). The Library
 * step is laid out like the Library page itself: a left rail of packs (the categories) and a right pane of the
 * selected pack's agents, server-paged + search-filtered. Picking one instantiates a working copy and hands the
 * new id back so the parent can open it. Warm-theme `.mdl` portal.
 */
export function NewAgentModal({ onCustom, onCreated, onClose }: NewAgentModalProps) {
  const [step, setStep] = useState<Step>("choose");
  const [pickedPackId, setPickedPackId] = useState<string | null>(null);

  const packs = usePacks();
  const instantiate = useInstantiateAgentFromStore();
  const pending = instantiate.isPending;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !pending) onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, pending]);

  function back() {
    instantiate.reset();   // drop any failed-attempt banner so it doesn't follow us back to the chooser
    setStep("choose");
  }

  function selectPack(id: string) {
    instantiate.reset();   // a prior pack's "couldn't add" banner must not follow the user onto the pack they switch to
    setPickedPackId(id);
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

  const eligiblePacks = packsWithAgents(packs.data ?? []);
  // Derived active pack (no effect): the explicit pick while it still has agents, else the first eligible pack — so
  // the rail opens with a selection and the agents pane is never blank while packs are available.
  const activePackId = (pickedPackId && eligiblePacks.some((p) => p.id === pickedPackId) ? pickedPackId : eligiblePacks[0]?.id) ?? null;

  // The wide rail+pane layout only earns its width once that split actually renders — keep the loading / error /
  // empty states at the normal modal width so they aren't a one-liner stranded in an over-wide shell.
  const showSplit = step === "library" && !packs.isLoading && !packs.isError && eligiblePacks.length > 0;

  const sub = step === "choose"
    ? "Start from scratch, or copy one from your Library."
    : "Pick a pack, then an agent to copy into a new working agent.";

  return createPortal(
    <>
      <div className="mdl-mask" onClick={() => { if (!pending) onClose(); }} />
      <div className={showSplit ? "mdl mdl-wide" : "mdl"} role="dialog" aria-modal="true">
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

          {step === "library" && (
            packs.isLoading ? <div className="wf-form-help">Loading your Library…</div>
            : packs.isError ? <div className="wf-form-help">Couldn't load your Library — try again.</div>
            : eligiblePacks.length === 0 ? <div className="wf-form-help">No agents in your Library yet — import a pack first.</div>
            : (
              <div className="na-lib">
                <div className="na-rail" role="listbox" aria-label="Packs">
                  {eligiblePacks.map((p) => (
                    <PackRailItem key={p.id} pack={p} active={p.id === activePackId} onSelect={() => selectPack(p.id)} />
                  ))}
                </div>
                {/* Keyed by pack: switching packs remounts the picker, so keepPreviousData can't leave the previous
                    pack's agents on screen (a fast click would otherwise instantiate the wrong pack's agent) and the
                    search + page reset for the new pack. */}
                {activePackId && <LibraryAgentPicker key={activePackId} packId={activePackId} pending={pending} onPick={pick} failed={instantiate.isError} />}
              </div>
            )
          )}
        </div>
      </div>
    </>,
    document.body,
  );
}

/** One pack in the modal's source rail — name + agent count, selectable by click or keyboard. */
function PackRailItem({ pack, active, onSelect }: { pack: PackSummary; active: boolean; onSelect: () => void }) {
  return (
    <div
      className="na-rail-item"
      role="option"
      tabIndex={0}
      aria-selected={active}
      data-active={active ? "true" : undefined}
      onClick={onSelect}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect(); } }}
    >
      <span className="na-rail-ic"><Ic.Box size={14} /></span>
      <div className="na-rail-body">
        <span className="na-rail-name" title={pack.name}>{pack.name}</span>
        <span className="na-rail-sub">{pack.agentCount} {pack.agentCount === 1 ? "agent" : "agents"}</span>
      </div>
    </div>
  );
}

/** The right pane: one server-side page of the selected pack's agents, search-filtered, each instantiable on click. */
function LibraryAgentPicker({ packId, pending, onPick, failed }: { packId: string; pending: boolean; onPick: (id: string) => void; failed: boolean }) {
  const [searchInput, setSearchInput] = useState("");
  const search = useDebounced(searchInput.trim(), 200);
  const [page, setPage] = useState(0);

  const agents = useListPackArtifacts(packId, "Agent", search, page, AGENT_PAGE_SIZE);
  const data = agents.data;

  function changeSearch(next: string) { setSearchInput(next); setPage(0); }

  return (
    <div className="na-pane">
      <div className="imp-search na-search">
        <Ic.Search size={14} />
        {/* No autoFocus: the picker remounts on every pack switch (keyed by pack), and autoFocus would yank focus
            out of the rail into the search box on each selection, breaking keyboard navigation through the rail. */}
        <input value={searchInput} onChange={(e) => changeSearch(e.target.value)} placeholder="Search agents…" aria-label="Search agents" />
      </div>

      <div className="na-list">
        {!data
          // A failed first load surfaces the error; a failed background refetch keeps the last good page on screen.
          ? (agents.isError ? <div className="wf-form-help">Couldn't load this pack's agents — try again.</div> : <div className="wf-form-help">Loading…</div>)
          : data.items.length === 0
            ? <div className="wf-form-help">{search ? "No agent matches your search." : "No agents in this pack."}</div>
            : (
              <div className="wf-add-choices">
                {data.items.map((a) => (
                  <button type="button" key={a.id} className="wf-add-choice" disabled={pending} onClick={() => onPick(a.id)}>
                    <span className="wf-add-choice-ic"><Ic.Bot size={20} /></span>
                    <span className="wf-add-choice-name">{a.name}</span>
                    <span className="wf-add-choice-desc">{a.description ?? `@${a.slug}`}</span>
                    <span className="wf-add-choice-arrow"><Ic.Plus size={16} /></span>
                  </button>
                ))}
              </div>
            )}
      </div>

      {data && data.pageCount > 1 && (
        <div className="lib-pager">
          <button type="button" className="lib-pager-btn" disabled={data.page === 0 || agents.isFetching} onClick={() => setPage(data.page - 1)} aria-label="Previous page"><Ic.ChevronLeft size={15} /></button>
          <span className="lib-pager-info">{data.page * AGENT_PAGE_SIZE + 1}–{Math.min((data.page + 1) * AGENT_PAGE_SIZE, data.total)} of {data.total}</span>
          <button type="button" className="lib-pager-btn" disabled={data.page >= data.pageCount - 1 || agents.isFetching} onClick={() => setPage(data.page + 1)} aria-label="Next page"><Ic.ChevronRight size={15} /></button>
        </div>
      )}

      {failed && <div className="wf-form-help" style={{ color: "var(--danger)" }}>Couldn't add that agent — try again.</div>}
    </div>
  );
}
