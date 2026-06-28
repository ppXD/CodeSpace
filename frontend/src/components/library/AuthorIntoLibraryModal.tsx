import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useAuthorStoreAgent } from "@/hooks/use-agents";
import { useAuthorStoreSkill } from "@/hooks/use-skills";

type Step = "choose" | "agent" | "skill";

/**
 * "Add to Library" — author a reusable agent or skill directly into the team's Custom pack (a store entry), rather
 * than onto the runnable bench. Step 1 chooses the kind; step 2 is a lean identity form. On save it's a Library
 * template — you instantiate a working copy (New-agent "from Library" / the skill-binding picker) to actually use it.
 * Warm-theme `.mdl` portal, mirroring Add-workflow / the agent editor's form fields.
 */
export function AuthorIntoLibraryModal({ onClose }: { onClose: () => void }) {
  const [step, setStep] = useState<Step>("choose");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [systemPrompt, setSystemPrompt] = useState("");   // agent only
  const [body, setBody] = useState("");                   // skill only
  const [category, setCategory] = useState("");           // skill only

  const authorAgent = useAuthorStoreAgent();
  const authorSkill = useAuthorStoreSkill();
  const pending = authorAgent.isPending || authorSkill.isPending;
  const error = authorAgent.error?.message ?? authorSkill.error?.message ?? null;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !pending) onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, pending]);

  const canSave = !pending && name.trim() !== "";

  async function save() {
    if (!canSave) return;
    try {
      if (step === "agent") await authorAgent.mutateAsync({ name, description: description || null, systemPrompt: systemPrompt || null });
      else if (step === "skill") await authorSkill.mutateAsync({ name, description: description || null, body: body || null, category: category || null });
      onClose();
    } catch {
      /* surfaced via the error banner below */
    }
  }

  const sub = step === "choose"
    ? "Add a reusable agent or skill to your Custom library."
    : step === "agent" ? "A reusable agent template — instantiate a copy to run it."
    : "A reusable skill — instantiate a copy to bind it to an agent.";

  return createPortal(
    <>
      <div className="mdl-mask" onClick={() => { if (!pending) onClose(); }} />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          {step !== "choose" && (
            <button type="button" className="mdl-back" onClick={() => setStep("choose")} title="Back"><Ic.ChevronLeft size={16} /></button>
          )}
          <div className="mdl-title-wrap">
            <div className="mdl-title">Add to Library</div>
            <div className="mdl-sub">{sub}</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          {error && (
            <div className="cn-banner cn-banner-err" style={{ marginBottom: 14 }}>
              <div className="cn-banner-h">Couldn't save</div>
              <div className="cn-banner-p">{error}</div>
            </div>
          )}

          {step === "choose" && (
            <div className="wf-add-choices">
              <button type="button" className="wf-add-choice" onClick={() => setStep("agent")}>
                <span className="wf-add-choice-ic"><Ic.Bot size={20} /></span>
                <span className="wf-add-choice-name">Agent</span>
                <span className="wf-add-choice-desc">A reusable agent template.</span>
                <span className="wf-add-choice-arrow"><Ic.ChevronRight size={16} /></span>
              </button>
              <button type="button" className="wf-add-choice" onClick={() => setStep("skill")}>
                <span className="wf-add-choice-ic"><Ic.Book size={20} /></span>
                <span className="wf-add-choice-name">Skill</span>
                <span className="wf-add-choice-desc">A reusable SKILL.md.</span>
                <span className="wf-add-choice-arrow"><Ic.ChevronRight size={16} /></span>
              </button>
            </div>
          )}

          {step !== "choose" && (
            <div className="wf-form">
              <div className="wf-form-row">
                <label className="wf-form-label" htmlFor="lib-name">Name<span className="wf-form-required">*</span></label>
                <input id="lib-name" className="wf-form-input" value={name} onChange={(e) => setName(e.target.value)} placeholder={step === "agent" ? "Security Reviewer" : "Threat Modeling"} autoFocus />
              </div>

              <div className="wf-form-row">
                <label className="wf-form-label" htmlFor="lib-desc">Description</label>
                <input id="lib-desc" className="wf-form-input" value={description} onChange={(e) => setDescription(e.target.value)} placeholder="When to use it." />
              </div>

              {step === "agent" ? (
                <div className="wf-form-row">
                  <label className="wf-form-label" htmlFor="lib-prompt">System prompt</label>
                  <textarea id="lib-prompt" className="wf-form-textarea" value={systemPrompt} onChange={(e) => setSystemPrompt(e.target.value)} rows={7} placeholder="You are a senior security reviewer. Audit for…" />
                </div>
              ) : (
                <>
                  <div className="wf-form-row">
                    <label className="wf-form-label" htmlFor="lib-body">SKILL.md</label>
                    <textarea id="lib-body" className="wf-form-textarea" value={body} onChange={(e) => setBody(e.target.value)} rows={8} placeholder="# Threat Modeling&#10;Use STRIDE to enumerate threats…" />
                  </div>
                  <div className="wf-form-row">
                    <label className="wf-form-label" htmlFor="lib-cat">Category</label>
                    <input id="lib-cat" className="wf-form-input" value={category} onChange={(e) => setCategory(e.target.value)} placeholder="security" />
                  </div>
                </>
              )}
            </div>
          )}
        </div>

        {step !== "choose" && (
          <div className="mdl-foot">
            <button className="btn" onClick={onClose} disabled={pending}>Cancel</button>
            <button className="btn btn-primary" disabled={!canSave} onClick={save}>{pending ? "Adding…" : "Add to Library"}</button>
          </div>
        )}
      </div>
    </>,
    document.body,
  );
}
