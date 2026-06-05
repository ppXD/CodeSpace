import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowTemplate } from "@/hooks/use-workflow-templates";

interface AddWorkflowModalProps {
  templates: WorkflowTemplate[];
  pending: boolean;
  onBlank: () => void;
  onTemplate: (template: WorkflowTemplate) => void;
  onClose: () => void;
}

/**
 * "Add workflow" dialog. Step 1 offers Blank vs Template; choosing Template flips, in place, to a
 * card grid of the available starter workflows. Warm-theme `.mdl` portal shell (matching Run / Add
 * repo). The template grid is data-driven from WORKFLOW_TEMPLATES — a new template appears here with
 * no change to this component.
 */
export function AddWorkflowModal({ templates, pending, onBlank, onTemplate, onClose }: AddWorkflowModalProps) {
  const [step, setStep] = useState<"choose" | "templates">("choose");

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            {step === "templates" && (
              <button type="button" className="wf-add-back" onClick={() => setStep("choose")}>
                <Ic.ArrowLeft size={13} /> Back
              </button>
            )}
            <div className="mdl-title">{step === "choose" ? "Add workflow" : "Choose a template"}</div>
            <div className="mdl-sub">
              {step === "choose"
                ? "Start from an empty canvas, or a ready-made workflow."
                : "Each is a ready-to-run starting point you can edit."}
            </div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          {step === "choose" ? (
            <div className="wf-add-choices">
              <button type="button" className="wf-add-choice" disabled={pending} onClick={onBlank}>
                <span className="wf-add-choice-ic"><Ic.Plus size={20} /></span>
                <span className="wf-add-choice-name">Blank</span>
                <span className="wf-add-choice-desc">An empty canvas — drag in the steps you want.</span>
              </button>
              <button type="button" className="wf-add-choice" disabled={pending} onClick={() => setStep("templates")}>
                <span className="wf-add-choice-ic"><Ic.Sparkles size={20} /></span>
                <span className="wf-add-choice-name">Template</span>
                <span className="wf-add-choice-desc">Start from a ready-made workflow.</span>
                <span className="wf-add-choice-arrow"><Ic.ChevronRight size={16} /></span>
              </button>
            </div>
          ) : (
            <div className="wf-tpl-cards">
              {templates.map((t) => (
                <button key={t.id} type="button" className="wf-tpl-card" disabled={pending} onClick={() => onTemplate(t)}>
                  <span className="wf-tpl-card-name">{t.name}</span>
                  <span className="wf-tpl-card-desc">{t.description}</span>
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
    </>,
    document.body,
  );
}
