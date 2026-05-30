import { Ic } from "@/_imported/ai-code-space/icons";

import type { ScopeSuggestion } from "./scope-introspection";
import { VariablePickerInput } from "./VariablePickerInput";

/**
 * The flow.loop inspector — mirrors Dify's Loop node config (loop variables, a termination
 * condition set, a max-iterations cap) instead of the generic Config/Inputs forms. It reads/writes
 * the loop node's `config` object (the on-disk LoopConfig shape the engine parses):
 *
 *   { loopVariables: [{ name, type, ref? | value?, update? }],
 *     termination: { logic: "and"|"or", conditions: [{ ref, op, value? }] },
 *     maxIterations: number }
 *
 * Variable / termination / update refs use the same {{}} picker as everywhere else. The body
 * subgraph itself is authored on the canvas (a follow-up) — this panel is only the loop's settings.
 */

const TYPES = ["String", "Number", "Boolean", "Object", "Array"];

// Operator vocabulary — labels mirror Dify's dropdown; values are the engine's CompareValues keys.
const OPERATORS: ReadonlyArray<{ value: string; label: string }> = [
  { value: "contains", label: "contains" },
  { value: "not_contains", label: "not contains" },
  { value: "startsWith", label: "starts with" },
  { value: "endsWith", label: "ends with" },
  { value: "eq", label: "is" },
  { value: "neq", label: "is not" },
  { value: "is_empty", label: "is empty" },
  { value: "is_not_empty", label: "is not empty" },
];
const UNARY_OPS = new Set(["is_empty", "is_not_empty"]);

interface LoopVariable { name?: string; type?: string; ref?: string; value?: unknown; update?: string }
interface LoopCondition { ref?: string; op?: string; value?: string }
interface LoopTermination { logic?: string; conditions?: LoopCondition[] }

export interface LoopEditorProps {
  config: Record<string, unknown>;
  onConfigChange: (next: Record<string, unknown>) => void;
  suggestions: ScopeSuggestion[];
}

export function LoopEditor({ config, onConfigChange, suggestions }: LoopEditorProps) {
  const variables: LoopVariable[] = Array.isArray(config.loopVariables) ? (config.loopVariables as LoopVariable[]) : [];
  const termination: LoopTermination = (config.termination && typeof config.termination === "object" ? config.termination : {}) as LoopTermination;
  const conditions: LoopCondition[] = Array.isArray(termination.conditions) ? termination.conditions : [];
  const logic = termination.logic === "or" ? "or" : "and";
  const maxIterations = typeof config.maxIterations === "number" ? config.maxIterations : 10;

  const setVariables = (next: LoopVariable[]) => onConfigChange({ ...config, loopVariables: next });
  const setTermination = (next: LoopTermination) => onConfigChange({ ...config, termination: next });
  const setConditions = (next: LoopCondition[]) => setTermination({ logic, conditions: next });

  const patchVar = (i: number, patch: Partial<LoopVariable>) =>
    setVariables(variables.map((v, idx) => (idx === i ? { ...v, ...patch } : v)));
  const patchCond = (i: number, patch: Partial<LoopCondition>) =>
    setConditions(conditions.map((c, idx) => (idx === i ? { ...c, ...patch } : c)));

  return (
    <>
      {/* ── Loop variables ───────────────────────────────────────────────── */}
      <section className="wf-inspector-section">
        <div className="wf-loop-section-head">
          <div className="wf-inspector-section-h">Loop variables</div>
          <button type="button" className="btn btn-ghost wf-loop-add" onClick={() => setVariables([...variables, { name: "", type: "String", value: "" }])}>
            <Ic.Plus size={12} /> Add
          </button>
        </div>
        <p className="wf-retry-hint">Mutable state carried across iterations. Read in the body as <code>{"{{loop.<name>}}"}</code>.</p>

        {variables.length === 0 && <div className="wf-loop-empty">No loop variables.</div>}

        {variables.map((v, i) => {
          const source = v.ref != null ? "variable" : "constant";
          return (
            <div key={i} className="wf-loop-var">
              <div className="wf-loop-var-row">
                <input
                  className="wf-form-input wf-loop-var-name"
                  placeholder="name"
                  value={v.name ?? ""}
                  onChange={(e) => patchVar(i, { name: e.target.value })}
                />
                <select className="wf-form-input wf-loop-var-type" value={v.type ?? "String"} onChange={(e) => patchVar(i, { type: e.target.value })}>
                  {TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                </select>
                <select
                  className="wf-form-input wf-loop-var-source"
                  value={source}
                  onChange={(e) => patchVar(i, e.target.value === "variable" ? { ref: "", value: undefined } : { ref: undefined, value: "" })}
                >
                  <option value="variable">Variable</option>
                  <option value="constant">Constant</option>
                </select>
                <button type="button" className="wf-loop-del" title="Remove" onClick={() => setVariables(variables.filter((_, idx) => idx !== i))}>
                  <Ic.Trash size={13} />
                </button>
              </div>

              {source === "variable" ? (
                <VariablePickerInput value={v.ref ?? ""} onChange={(next) => patchVar(i, { ref: next })} suggestions={suggestions} placeholder="Initial value — pick a variable" />
              ) : (
                <input className="wf-form-input" placeholder="Initial constant value" value={typeof v.value === "string" ? v.value : ""} onChange={(e) => patchVar(i, { value: e.target.value })} />
              )}

              <VariablePickerInput value={v.update ?? ""} onChange={(next) => patchVar(i, { update: next || undefined })} suggestions={suggestions} placeholder="Update each iteration (optional) — e.g. {{loop.x}}:{{loop.index}}" />
            </div>
          );
        })}
      </section>

      {/* ── Termination condition ────────────────────────────────────────── */}
      <section className="wf-inspector-section">
        <div className="wf-loop-section-head">
          <div className="wf-inspector-section-h">Termination condition</div>
          <button type="button" className="btn btn-ghost wf-loop-add" onClick={() => setConditions([...conditions, { ref: "", op: "contains", value: "" }])}>
            <Ic.Plus size={12} /> Add
          </button>
        </div>
        <p className="wf-retry-hint">The loop stops when {conditions.length > 1 ? "these conditions" : "this condition"} are met (or the cap is hit). Checked at the end of each pass.</p>

        {conditions.length > 1 && (
          <div className="wf-loop-logic">
            <span>Match</span>
            <select className="wf-form-input wf-loop-logic-sel" value={logic} onChange={(e) => setTermination({ logic: e.target.value, conditions })}>
              <option value="and">all (and)</option>
              <option value="or">any (or)</option>
            </select>
          </div>
        )}

        {conditions.length === 0 && <div className="wf-loop-empty">No condition — the loop runs until the max-iterations cap.</div>}

        {conditions.map((c, i) => (
          <div key={i} className="wf-loop-cond">
            <VariablePickerInput value={c.ref ?? ""} onChange={(next) => patchCond(i, { ref: next })} suggestions={suggestions} placeholder="Value to check — e.g. {{nodes.llm.outputs.text}}" />
            <div className="wf-loop-cond-row">
              <select className="wf-form-input wf-loop-cond-op" value={c.op ?? "contains"} onChange={(e) => patchCond(i, { op: e.target.value })}>
                {OPERATORS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
              </select>
              {!UNARY_OPS.has(c.op ?? "contains") && (
                <input className="wf-form-input wf-loop-cond-val" placeholder="value" value={c.value ?? ""} onChange={(e) => patchCond(i, { value: e.target.value })} />
              )}
              <button type="button" className="wf-loop-del" title="Remove" onClick={() => setConditions(conditions.filter((_, idx) => idx !== i))}>
                <Ic.Trash size={13} />
              </button>
            </div>
          </div>
        ))}
      </section>

      {/* ── Max iterations ───────────────────────────────────────────────── */}
      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Max iterations</div>
        <div className="wf-loop-max">
          <input
            type="number"
            min={1}
            max={1000}
            className="wf-form-input wf-loop-max-num"
            value={maxIterations}
            onChange={(e) => onConfigChange({ ...config, maxIterations: clampMax(e.target.value) })}
          />
          <input
            type="range"
            min={1}
            max={100}
            className="wf-loop-max-slider"
            value={Math.min(maxIterations, 100)}
            onChange={(e) => onConfigChange({ ...config, maxIterations: clampMax(e.target.value) })}
          />
        </div>
        <p className="wf-retry-hint">Hard safety cap — the loop never runs more than this many passes (engine ceiling 1000).</p>
      </section>
    </>
  );
}

function clampMax(raw: string): number {
  const n = Math.floor(Number(raw));
  if (!Number.isFinite(n)) return 10;
  return Math.min(1000, Math.max(1, n));
}
