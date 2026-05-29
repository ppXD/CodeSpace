import { useEffect, useMemo, useState } from "react";
import { createPortal } from "react-dom";

import type { WorkflowVariable } from "@/api/workflows";
import { Ic } from "@/_imported/ai-code-space/icons";
import { buildRunInputForm } from "@/lib/runInputForm";

import { SchemaForm } from "./SchemaForm";

interface RunWorkflowModalProps {
  workflowName: string;
  inputs: WorkflowVariable[];
  pending: boolean;
  error?: string | null;
  onRun: (payload: Record<string, unknown>) => void;
  onClose: () => void;
}

/**
 * "Run workflow" dialog for a manual run — the Dify-style "fill the form, then run" step.
 * Renders the workflow's declared inputs through the same `SchemaForm` the editor uses, then
 * hands the collected `{name: value}` object to `onRun` as the run payload (the engine maps it
 * by-name onto `{{input.*}}`). Only shown when the workflow declares inputs; an input-less
 * workflow runs immediately without this dialog.
 *
 * Uses the warm-theme `.mdl` portal shell (matching Add-repo / Connect-remote) rather than the
 * Tailwind dialog primitive, so it sits consistently inside the workflow UI.
 */
export function RunWorkflowModal({ workflowName, inputs, pending, error, onRun, onClose }: RunWorkflowModalProps) {
  const { schema, initialValues } = useMemo(() => buildRunInputForm(inputs), [inputs]);
  const [values, setValues] = useState<Record<string, unknown>>(initialValues);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const missingRequired = schema.required.filter((name) => isBlank(values[name]));
  const canRun = !pending && missingRequired.length === 0;

  return createPortal(
    <>
      <div className="mdl-mask" />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Run workflow</div>
            <div className="mdl-sub">Provide inputs for this run of “{workflowName}”.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          <SchemaForm schema={schema} value={values} onChange={setValues} />
        </div>

        <div className="mdl-foot">
          <div className="mdl-foot-info">
            {error
              ? error
              : missingRequired.length > 0
                ? `Fill required: ${missingRequired.join(", ")}`
                : "Values are available downstream as {{input.<name>}}."}
          </div>
          <button className="btn btn-primary" disabled={!canRun} onClick={() => onRun(values)}>
            <Ic.Play size={13} /> {pending ? "Starting…" : "Run"}
          </button>
        </div>
      </div>
    </>,
    document.body,
  );
}

/** A required field counts as unfilled when null/undefined or a whitespace-only string. */
function isBlank(value: unknown): boolean {
  return value === undefined || value === null || (typeof value === "string" && value.trim() === "");
}
