import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowTemplate } from "@/hooks/use-workflow-templates";

interface AddWorkflowModalProps {
  templates: WorkflowTemplate[];
  pending: boolean;
  onBlank: () => void;
  onTask: (task: string) => void;
  onTemplate: (template: WorkflowTemplate) => void;
  onClose: () => void;
}

type Step = "choose" | "describe" | "templates";

const HEAD: Record<Step, { title: string; sub: string }> = {
  choose: { title: "Add workflow", sub: "Describe what it should do, start blank, or pick a template." },
  describe: { title: "Describe a task", sub: "Say what this workflow should do, in plain language." },
  templates: { title: "Choose a template", sub: "Each is a ready-to-run starting point you can edit." },
};

/**
 * "Add workflow" dialog. Step 1 offers three on-ramps: Describe a task (the default —
 * capture intent in plain language), Blank (an empty canvas), or Template (a ready-made workflow).
 * Choosing Describe flips, in place, to a task textarea; choosing Template flips to a card grid.
 * Warm-theme `.mdl` portal shell. The template grid is data-driven from WORKFLOW_TEMPLATES — a new
 * template appears here with no change to this component.
 */
export function AddWorkflowModal({ templates, pending, onBlank, onTask, onTemplate, onClose }: AddWorkflowModalProps) {
  const [step, setStep] = useState<Step>("choose");
  const [task, setTask] = useState("");

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const canCreate = !pending && task.trim() !== "";

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          {step !== "choose" && (
            <button type="button" className="mdl-back" onClick={() => setStep("choose")} title="Back"><Ic.ChevronLeft size={16} /></button>
          )}
          <div className="mdl-title-wrap">
            <div className="mdl-title">{HEAD[step].title}</div>
            <div className="mdl-sub">{HEAD[step].sub}</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          {step === "choose" && (
            <div className="wf-add-choices">
              <button type="button" className="wf-add-choice" disabled={pending} onClick={() => setStep("describe")}>
                <span className="wf-add-choice-ic"><Ic.Bot size={20} /></span>
                <span className="wf-add-choice-name">Describe a task</span>
                <span className="wf-add-choice-desc">Say what you want in plain language.</span>
                <span className="wf-add-choice-arrow"><Ic.ChevronRight size={16} /></span>
              </button>
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
          )}

          {step === "describe" && (
            <div className="wf-form">
              <div className="wf-form-row">
                <span className="wf-form-label">What should this workflow do?</span>
                <textarea
                  className="wf-form-textarea"
                  value={task}
                  onChange={(e) => setTask(e.target.value)}
                  placeholder="e.g. Review every PR for security issues and post a summary to the team channel."
                  rows={4}
                  autoFocus
                />
                <span className="wf-form-help">We'll name it from this — you can rename and build out the steps in the editor.</span>
              </div>
            </div>
          )}

          {step === "templates" && (
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

        {step === "describe" && (
          <div className="mdl-foot">
            <button className="btn" onClick={onClose}>Cancel</button>
            <button className="btn btn-primary" disabled={!canCreate} onClick={() => onTask(task)}>
              {pending ? "Creating…" : "Create workflow"}
            </button>
          </div>
        )}
      </div>
    </>,
    document.body,
  );
}
