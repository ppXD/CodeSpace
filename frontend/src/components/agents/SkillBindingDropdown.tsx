import { useEffect, useRef, useState, type Dispatch, type SetStateAction } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PackSummary } from "@/api/packs";
import { useDebounced } from "@/hooks/use-debounced";
import { useListPackArtifacts, usePacks } from "@/hooks/use-packs";
import { useInstantiateSkillFromStore } from "@/hooks/use-skills";

import { packsWithSkills } from "./newAgentPicker";

const SKILL_PAGE_SIZE = 8;

/**
 * The agent editor's skill-binding control: an inline multi-select dropdown laid out left-right like the Library —
 * a pack rail and the selected pack's skills (server-paged + searchable). It replaces the old popped picker modal,
 * so several skills can be toggled in one open. Picking an un-added skill instantiates a working copy of the store
 * skill and binds it; toggling it off unbinds (the working copy is reused if it's re-picked, never re-minted).
 *
 * `onChange` is the parent's state setter — it accepts a functional updater, which is load-bearing: an awaited mint
 * must append to the CURRENT bound set, not a snapshot captured before the await (else a skill the user removed mid-
 * flight would be resurrected). All the binding bookkeeping (`bySource`, the instantiate mutation, the picked pack)
 * lives on THIS component, which stays mounted across open/close — only the panel below unmounts — so re-opening the
 * dropdown still knows which skills it minted this session (no duplicate copies, correct check state).
 */
export function SkillBindingDropdown({ selected, onChange, labelFor }: { selected: string[]; onChange: Dispatch<SetStateAction<string[]>>; labelFor: (id: string) => string }) {
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  const packs = usePacks();
  const instantiate = useInstantiateSkillFromStore();

  // store-skill id → the working copy we minted for it this session. Kept even after an unbind so a re-pick rebinds
  // the SAME copy instead of minting a duplicate. The check state derives from `selected`, so an externally-removed
  // chip (or an unbind here) shows unchecked with no extra bookkeeping.
  const [bySource, setBySource] = useState<Map<string, string>>(() => new Map());

  const eligiblePacks = packsWithSkills(packs.data ?? []);
  const [pickedPackId, setPickedPackId] = useState<string | null>(null);
  const activePackId = (pickedPackId && eligiblePacks.some((p) => p.id === pickedPackId) ? pickedPackId : eligiblePacks[0]?.id) ?? null;

  // Close on a click outside the control — it's inline (no modal mask of its own). Clicking the editor's own mask
  // still tears down the whole editor; clicks elsewhere inside the editor body collapse just this dropdown.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => { if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false); };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [open]);

  function isChecked(sourceId: string): boolean {
    const working = bySource.get(sourceId);
    return !!working && selected.includes(working);
  }

  function selectPack(id: string) {
    instantiate.reset();   // clear a prior pack's "couldn't add" line so it doesn't show under the pack we switch to
    setPickedPackId(id);
  }

  async function toggle(sourceId: string) {
    const working = bySource.get(sourceId);

    if (working && selected.includes(working)) { onChange((prev) => prev.filter((x) => x !== working)); return; }                 // unbind
    if (working) { onChange((prev) => (prev.includes(working) ? prev : [...prev, working])); return; }                            // rebind the reused copy

    if (instantiate.isPending) return;                                                                                            // new source → mint + bind
    try {
      const { id } = await instantiate.mutateAsync(sourceId);
      setBySource((m) => new Map(m).set(sourceId, id));
      onChange((prev) => [...prev, id]);   // functional: append to the CURRENT set so a mid-flight removal isn't undone
    } catch {
      /* surfaced via the error line in the pane */
    }
  }

  const pendingSource = instantiate.isPending ? (instantiate.variables ?? null) : null;

  return (
    <div className="skb" ref={wrapRef}>
      {/* A token-input control: the bound skills sit INSIDE the field as removable labels; clicking the field opens
          the picker. role=button (not a real button) so the per-chip remove buttons can nest validly. */}
      <div
        className="skb-control"
        role="button"
        tabIndex={0}
        aria-expanded={open}
        aria-label="Bound skills"
        data-open={open}
        onClick={() => setOpen((o) => !o)}
        // Only the field itself toggles on Enter/Space — a key on a nested remove button (e.target !== the field)
        // must not also toggle the picker, and must keep its own native activation (no preventDefault stealing it).
        onKeyDown={(e) => { if (e.target === e.currentTarget && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); setOpen((o) => !o); } }}
      >
        {selected.length === 0
          ? <span className="skb-placeholder">Add skills…</span>
          : selected.map((id) => (
              <span key={id} className="wf-trigger-chip skb-chip">
                {labelFor(id)}
                <button type="button" className="ed-tok-x" aria-label={`Remove ${labelFor(id)}`} onClick={(e) => { e.stopPropagation(); onChange((prev) => prev.filter((x) => x !== id)); }}><Ic.X size={11} /></button>
              </span>
            ))}
        <Ic.ChevronDown size={14} className="skb-caret" data-open={open} />
      </div>

      {open && (
        packs.isLoading ? <div className="skb-panel skb-panel-msg"><div className="wf-form-help">Loading your Library…</div></div>
        : packs.isError ? <div className="skb-panel skb-panel-msg"><div className="wf-form-help">Couldn't load your Library — try again.</div></div>
        : eligiblePacks.length === 0 ? <div className="skb-panel skb-panel-msg"><div className="wf-form-help">No skills in your Library yet — import a pack or add a custom skill first.</div></div>
        : (
          <div className="skb-panel">
            <div className="skb-rail" role="listbox" aria-label="Packs">
              {eligiblePacks.map((p) => (
                <PackRailItem key={p.id} pack={p} active={p.id === activePackId} onSelect={() => selectPack(p.id)} />
              ))}
            </div>
            {/* Keyed by pack: switching packs remounts the skills list (fresh search/page, no stale-pack rows). The
                instantiate bookkeeping lives on the dropdown above, so it persists across packs AND across re-opens. */}
            {activePackId && (
              <PackSkills
                key={activePackId}
                packId={activePackId}
                isChecked={isChecked}
                pendingSource={pendingSource}
                minting={instantiate.isPending}
                failed={instantiate.isError}
                onToggle={toggle}
              />
            )}
          </div>
        )
      )}
    </div>
  );
}

/** One pack in the dropdown's rail — name + skill count, selectable by click or keyboard. */
function PackRailItem({ pack, active, onSelect }: { pack: PackSummary; active: boolean; onSelect: () => void }) {
  return (
    <div
      className="skb-rail-item"
      role="option"
      tabIndex={0}
      aria-selected={active}
      data-active={active ? "true" : undefined}
      onClick={onSelect}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect(); } }}
    >
      <span className="skb-rail-name" title={pack.name}>{pack.name}</span>
      <span className="skb-rail-sub">{pack.skillCount}</span>
    </div>
  );
}

/** The right pane: one server-side page of the selected pack's skills, each a toggle (checked = currently bound). */
function PackSkills({ packId, isChecked, pendingSource, minting, failed, onToggle }: { packId: string; isChecked: (id: string) => boolean; pendingSource: string | null; minting: boolean; failed: boolean; onToggle: (id: string) => void }) {
  const [searchInput, setSearchInput] = useState("");
  const search = useDebounced(searchInput.trim(), 200);
  const [page, setPage] = useState(0);

  const skills = useListPackArtifacts(packId, "Skill", search, page, SKILL_PAGE_SIZE);
  const data = skills.data;

  function changeSearch(next: string) { setSearchInput(next); setPage(0); }

  return (
    <div className="skb-pane">
      <div className="imp-search skb-search">
        <Ic.Search size={14} />
        <input value={searchInput} onChange={(e) => changeSearch(e.target.value)} placeholder="Search skills…" aria-label="Search skills" />
      </div>

      <div className="skb-list">
        {!data
          ? (skills.isError ? <div className="wf-form-help">Couldn't load this pack's skills — try again.</div> : <div className="wf-form-help">Loading…</div>)
          : data.items.length === 0
            ? <div className="wf-form-help">{search ? "No skill matches your search." : "No skills in this pack."}</div>
            : data.items.map((s) => {
                const on = isChecked(s.id);
                // While a mint is in flight, lock the un-added rows too (they'd mint a second copy) so they don't look
                // clickable-but-inert; an already-bound row stays enabled so it can still be unbound.
                return (
                  <button type="button" key={s.id} className="skb-skill" data-on={on} disabled={pendingSource === s.id || (minting && !on)} aria-pressed={on} onClick={() => onToggle(s.id)}>
                    <span className="skb-check">{on ? <Ic.Check size={13} /> : null}</span>
                    <span className="skb-skill-name">{s.name}</span>
                    <span className="skb-skill-handle">@{s.slug}</span>
                  </button>
                );
              })}
      </div>

      {data && data.pageCount > 1 && (
        <div className="lib-pager">
          <button type="button" className="lib-pager-btn" disabled={data.page === 0 || skills.isFetching} onClick={() => setPage(data.page - 1)} aria-label="Previous page"><Ic.ChevronLeft size={15} /></button>
          <span className="lib-pager-info">{data.page * SKILL_PAGE_SIZE + 1}–{Math.min((data.page + 1) * SKILL_PAGE_SIZE, data.total)} of {data.total}</span>
          <button type="button" className="lib-pager-btn" disabled={data.page >= data.pageCount - 1 || skills.isFetching} onClick={() => setPage(data.page + 1)} aria-label="Next page"><Ic.ChevronRight size={15} /></button>
        </div>
      )}

      {failed && <div className="wf-form-help" style={{ color: "var(--danger)" }}>Couldn't add that skill — try again.</div>}
    </div>
  );
}
