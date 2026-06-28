import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { paginate } from "@/components/library/libraryView";
import { usePack, usePacks } from "@/hooks/use-packs";
import { useInstantiateSkillFromStore } from "@/hooks/use-skills";
import { packsWithSkills, skillArtifacts } from "./newAgentPicker";

const PAGE = 8;

/**
 * The agent editor's skill-binding picker — browse the Library by pack → skill (search + paginate, since a pack can
 * hold hundreds), and on pick instantiate a working copy of the store skill and hand its id back so the editor binds
 * it. This is the "instantiate to use" model for skills, mirroring the New-agent "from Library" picker. `.mdl` portal.
 */
export function SkillLibraryPickerModal({ onPicked, onClose }: { onPicked: (workingSkillId: string) => void; onClose: () => void }) {
  const [packId, setPackId] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(0);

  const packs = usePacks();
  const pack = usePack(packId);
  const instantiate = useInstantiateSkillFromStore();
  const pending = instantiate.isPending;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !pending) onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, pending]);

  function back() { instantiate.reset(); setPackId(null); setQuery(""); setPage(0); }

  async function pick(storeSkillId: string) {
    if (pending) return;
    try {
      const { id } = await instantiate.mutateAsync(storeSkillId);
      onPicked(id);
    } catch {
      /* surfaced via the error line below */
    }
  }

  const eligiblePacks = packsWithSkills(packs.data ?? []);
  // Trust the detail only when it's for the SELECTED pack — usePack keeps the previous pack's data during a switch.
  const packReady = !!packId && pack.data?.pack.id === packId;
  const paged = paginate(packReady ? skillArtifacts(pack.data!.artifacts, query) : [], page, PAGE);

  return createPortal(
    <>
      <div className="mdl-mask" onClick={() => { if (!pending) onClose(); }} />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          {packId && <button type="button" className="mdl-back" onClick={back} title="Back"><Ic.ChevronLeft size={16} /></button>}
          <div className="mdl-title-wrap">
            <div className="mdl-title">Add a skill</div>
            <div className="mdl-sub">{packId ? "Pick a skill to copy + bind to this agent." : "Choose a Library pack."}</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          {!packId && (
            packs.isLoading ? <div className="wf-form-help">Loading your Library…</div>
            : packs.isError ? <div className="wf-form-help">Couldn't load your Library — try again.</div>
            : eligiblePacks.length === 0 ? <div className="wf-form-help">No skills in your Library yet — import a pack or add a custom skill first.</div>
            : (
              <div className="wf-add-choices">
                {eligiblePacks.map((p) => (
                  <button type="button" key={p.id} className="wf-add-choice" onClick={() => { setPackId(p.id); setPage(0); }}>
                    <span className="wf-add-choice-ic"><Ic.Box size={20} /></span>
                    <span className="wf-add-choice-name">{p.name}</span>
                    <span className="wf-add-choice-desc">{p.skillCount} {p.skillCount === 1 ? "skill" : "skills"}</span>
                    <span className="wf-add-choice-arrow"><Ic.ChevronRight size={16} /></span>
                  </button>
                ))}
              </div>
            )
          )}

          {packId && (
            <>
              <div className="imp-search" style={{ maxWidth: "none", marginBottom: 12 }}>
                <Ic.Search size={14} />
                <input value={query} onChange={(e) => { setQuery(e.target.value); setPage(0); }} placeholder="Search skills…" aria-label="Search skills" autoFocus />
              </div>
              {!packReady ? <div className="wf-form-help">Loading…</div>
              : paged.items.length === 0 ? <div className="wf-form-help">No skill matches your search.</div>
              : (
                <div className="wf-add-choices">
                  {paged.items.map((a) => (
                    <button type="button" key={a.id} className="wf-add-choice" disabled={pending} onClick={() => pick(a.id)}>
                      <span className="wf-add-choice-ic"><Ic.Book size={20} /></span>
                      <span className="wf-add-choice-name">{a.name}</span>
                      <span className="wf-add-choice-desc">{a.description ?? `@${a.slug}`}</span>
                      <span className="wf-add-choice-arrow"><Ic.Plus size={16} /></span>
                    </button>
                  ))}
                </div>
              )}
              {paged.pageCount > 1 && (
                <div className="lib-pager">
                  <button type="button" className="lib-pager-btn" disabled={paged.page === 0} onClick={() => setPage(paged.page - 1)} aria-label="Previous page"><Ic.ChevronLeft size={15} /></button>
                  <span className="lib-pager-info">{paged.page * PAGE + 1}–{Math.min((paged.page + 1) * PAGE, paged.total)} of {paged.total}</span>
                  <button type="button" className="lib-pager-btn" disabled={paged.page >= paged.pageCount - 1} onClick={() => setPage(paged.page + 1)} aria-label="Next page"><Ic.ChevronRight size={15} /></button>
                </div>
              )}
              {instantiate.isError && <div className="wf-form-help" style={{ color: "var(--danger)" }}>Couldn't add that skill — try again.</div>}
            </>
          )}
        </div>
      </div>
    </>,
    document.body,
  );
}
