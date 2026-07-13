import { useEffect, useMemo, useRef, useState } from "react";

import type { ScopeSuggestion } from "./scope-introspection";
import { VariablePickerInput } from "./VariablePickerInput";
import { allowedOperators, IF_OPERATORS, parseCondition, serializeCondition, type IfCondition } from "./ifCondition";

export interface LogicIfEditorProps {
  config: Record<string, unknown>;
  onConfigChange: (next: Record<string, unknown>) => void;
  suggestions: ScopeSuggestion[];
}

/**
 * Guided editor for logic.if's condition — a value, an operator dropdown, and a value/text, instead of the
 * hand-written expression DSL. It reads + writes the SAME `condition` string the engine evaluates (via
 * ifCondition's parse/serialize), so nothing about the engine changes and an existing condition opens straight
 * into the three parts. The operator list narrows to the left value's type (a Yes/No only offers is-true /
 * equals), and an "edit as expression" escape hatch stays for anything the guided form can't express.
 */
export function LogicIfEditor({ config, onConfigChange, suggestions }: LogicIfEditorProps) {
  const condition = typeof config.condition === "string" ? config.condition : "";

  // The structured condition is LOCAL state, not re-derived from the string on every render. An in-progress
  // edit — a binary operator chosen BEFORE its compare-to value is typed — serializes to a bare `left`, which
  // would parse back to "is true" and snap the operator (and hide the Compare-to field, so the value could
  // never be entered). Seed from the string; re-sync only when it changes from OUTSIDE this editor (a raw
  // edit, an undo, a different node) — tracked via lastWritten so our own writes don't trigger a re-parse.
  const [cond, setCond] = useState<IfCondition>(() => parseCondition(condition));
  const lastWritten = useRef(condition);
  useEffect(() => {
    if (condition !== lastWritten.current) { setCond(parseCondition(condition)); lastWritten.current = condition; }
  }, [condition]);
  const [raw, setRaw] = useState(false);

  const write = (next: IfCondition) => {
    const serialized = serializeCondition(next);
    lastWritten.current = serialized;
    setCond(next);
    onConfigChange({ ...config, condition: serialized });
  };

  // When the left value is a {{ref}} we recognise, narrow the operators to its type — but always keep the
  // currently-saved op selectable so switching the left never silently drops it.
  const leftType = useMemo(() => suggestions.find((s) => `{{${s.path}}}` === cond.left.trim())?.type, [suggestions, cond.left]);
  const options = useMemo(() => {
    const allowed = allowedOperators(leftType);
    if (allowed.some((o) => o.value === cond.op)) return allowed;
    const current = IF_OPERATORS.find((o) => o.value === cond.op);
    return current ? [current, ...allowed] : allowed;
  }, [leftType, cond.op]);

  const arity = IF_OPERATORS.find((o) => o.value === cond.op)?.arity ?? "truthy";
  const preview = serializeCondition(cond);

  if (raw) {
    return (
      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Condition</div>
        <p className="wf-form-help">The raw expression the engine evaluates. Route to <b>true</b> when it holds.</p>
        <VariablePickerInput
          value={condition}
          onChange={(next) => onConfigChange({ ...config, condition: next })}
          suggestions={suggestions}
          placeholder={'e.g.  {{trigger.state}} == "open"'}
        />
        <button type="button" className="wf-linkish" onClick={() => setRaw(false)}>← Back to the guided editor</button>
      </section>
    );
  }

  return (
    <section className="wf-inspector-section wf-if">
      <div className="wf-inspector-section-h">Condition</div>
      <p className="wf-form-help">Go down the <b>true</b> path when this holds, otherwise <b>false</b>.</p>

      <div className="wf-form-row">
        <span className="wf-form-label">Value</span>
        <VariablePickerInput
          value={cond.left}
          onChange={(left) => write({ ...cond, left })}
          suggestions={suggestions}
          placeholder="pick a value — type @"
        />
      </div>

      <div className="wf-form-row">
        <span className="wf-form-label">Condition</span>
        <select className="wf-form-input wf-if-op" value={cond.op} onChange={(e) => write({ ...cond, op: e.target.value })}>
          {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
      </div>

      {arity === "binary" && (
        <div className="wf-form-row">
          <span className="wf-form-label">Compare to</span>
          <VariablePickerInput
            value={cond.right}
            onChange={(right) => write({ ...cond, right })}
            suggestions={suggestions}
            placeholder="a value or some text"
          />
        </div>
      )}

      {preview && <p className="wf-form-help wf-if-preview">Runs as <code>{preview}</code></p>}
      <button type="button" className="wf-linkish" onClick={() => setRaw(true)}>Edit as an expression instead</button>
    </section>
  );
}
