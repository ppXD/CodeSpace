import { useMemo, useState } from "react";

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
  const parsed = useMemo(() => parseCondition(condition), [condition]);
  const [raw, setRaw] = useState(false);

  const write = (next: IfCondition) => onConfigChange({ ...config, condition: serializeCondition(next) });

  // When the left value is a {{ref}} we recognise, narrow the operators to its type — but always keep the
  // currently-saved op selectable so switching the left never silently drops it.
  const leftType = useMemo(() => suggestions.find((s) => `{{${s.path}}}` === parsed.left.trim())?.type, [suggestions, parsed.left]);
  const options = useMemo(() => {
    const allowed = allowedOperators(leftType);
    if (allowed.some((o) => o.value === parsed.op)) return allowed;
    const current = IF_OPERATORS.find((o) => o.value === parsed.op);
    return current ? [current, ...allowed] : allowed;
  }, [leftType, parsed.op]);

  const arity = IF_OPERATORS.find((o) => o.value === parsed.op)?.arity ?? "truthy";
  const preview = serializeCondition(parsed);

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
          value={parsed.left}
          onChange={(left) => write({ ...parsed, left })}
          suggestions={suggestions}
          placeholder="pick a value — type @"
        />
      </div>

      <div className="wf-form-row">
        <span className="wf-form-label">Condition</span>
        <select className="wf-form-input wf-if-op" value={parsed.op} onChange={(e) => write({ ...parsed, op: e.target.value })}>
          {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
      </div>

      {arity === "binary" && (
        <div className="wf-form-row">
          <span className="wf-form-label">Compare to</span>
          <VariablePickerInput
            value={parsed.right}
            onChange={(right) => write({ ...parsed, right })}
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
