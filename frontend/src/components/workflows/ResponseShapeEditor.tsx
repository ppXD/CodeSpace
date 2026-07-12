import { useMemo } from "react";

import { countLeafFields, inferSchemaFromSample } from "./jsonShape";

export interface ResponseShapeEditorProps {
  config: Record<string, unknown>;
  onConfigChange: (next: Record<string, unknown>) => void;
}

/**
 * For a node whose response is dynamic (HTTP body / LLM json — manifest `x-dynamic-output`), the fields can't
 * be known ahead of time. The author pastes a SAMPLE response here; its shape is inferred and stored on
 * `config.responseSample`, and the {{ref}} picker then drills the real fields (Response → Customer → Email).
 * Editor-only — the engine ignores `responseSample`, so nothing about the run changes.
 */
export function ResponseShapeEditor({ config, onConfigChange }: ResponseShapeEditorProps) {
  const sample = typeof config.responseSample === "string" ? config.responseSample : "";
  const inferred = useMemo(() => inferSchemaFromSample(sample), [sample]);
  const count = countLeafFields(inferred);
  const typed = sample.trim().length > 0;

  return (
    <section className="wf-inspector-section">
      <div className="wf-inspector-section-h">Response shape</div>
      <p className="wf-form-help">
        The response is dynamic, so its fields aren&apos;t known ahead of time. Paste a sample response and the
        picker can drill into it (<b>Response → Customer → Email</b>). Editor-only — it never changes what runs.
      </p>
      <textarea
        className="wf-form-textarea"
        rows={5}
        value={sample}
        onChange={(e) => onConfigChange({ ...config, responseSample: e.target.value === "" ? undefined : e.target.value })}
        placeholder={'{\n  "customer": { "email": "a@b.com" }\n}'}
        spellCheck={false}
      />
      {typed && (inferred
        ? <p className="wf-form-help wf-shape-ok">✓ Detected — {count} field{count === 1 ? "" : "s"} now drillable in the picker.</p>
        : <p className="wf-retry-hint">Not valid JSON yet — paste a complete sample response.</p>)}
    </section>
  );
}
