import type { ScopeSuggestion } from "./scope-introspection";
import { VariablePickerInput } from "./VariablePickerInput";

/**
 * The flow.map inspector — a fan-out container. It binds a collection (the node's `items` INPUT, an
 * array {{...}} ref) and runs its body subgraph once per element, in bounded-parallel branches, then
 * reduces each branch's terminal output into a keyed array.
 *
 * It writes two on-disk shapes (matching the backend FlowMapNode manifest):
 *   - the `items` collection ref lives in the node's INPUTS (`{ items: "{{nodes.planner.outputs.json.subtasks}}" }`),
 *     declared in the node's InputSchema — so it's resolved at runtime like flow.iterate's collection;
 *   - `maxParallelism` (int? 1..64, inherit-when-empty), `errorHandling` ("terminate"|"continue") and
 *     `resultKey` (string, default "results") live in the node's CONFIG (the MapConfig shape the engine parses).
 *
 * The body subgraph itself is authored on the canvas (React Flow parent/child) — this panel is only the
 * map's settings, mirroring LoopEditor.
 */

export interface MapEditorProps {
  config: Record<string, unknown>;
  inputs: Record<string, unknown>;
  onConfigChange: (next: Record<string, unknown>) => void;
  onInputsChange: (next: Record<string, unknown>) => void;
  suggestions: ScopeSuggestion[];
}

export function MapEditor({ config, inputs, onConfigChange, onInputsChange, suggestions }: MapEditorProps) {
  const items = typeof inputs.items === "string" ? inputs.items : "";
  const errorHandling = config.errorHandling === "continue" ? "continue" : "terminate";
  const maxParallelism = typeof config.maxParallelism === "number" ? config.maxParallelism : undefined;
  const resultKey = typeof config.resultKey === "string" ? config.resultKey : "";

  // Empty input ⇒ remove the key (inherit the engine-wide default); a value clamps to [1, 64].
  const setMaxParallelism = (raw: string) => {
    const next = { ...config };
    const n = Math.floor(Number(raw));
    if (raw.trim() === "" || !Number.isFinite(n)) delete next.maxParallelism;
    else next.maxParallelism = Math.min(64, Math.max(1, n));
    onConfigChange(next);
  };

  // Empty result key ⇒ remove it (the engine defaults to "results"); otherwise store the literal.
  const setResultKey = (raw: string) => {
    const next = { ...config };
    if (raw.trim() === "") delete next.resultKey;
    else next.resultKey = raw;
    onConfigChange(next);
  };

  return (
    <>
      {/* ── Collection (items) ────────────────────────────────────────────── */}
      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Collection</div>
        <VariablePickerInput
          value={items}
          onChange={(next) => onInputsChange({ ...inputs, items: next })}
          suggestions={suggestions}
          placeholder="Array to fan out — e.g. {{nodes.planner.outputs.json.subtasks}}"
        />
        <p className="wf-retry-hint">
          The body runs once per element of this array, in parallel branches. Each branch reads its element as <code>{"{{item}}"}</code> / <code>{"{{index}}"}</code>. An empty array fans out zero branches (a valid no-op).
        </p>
      </section>

      {/* ── Branch parallelism ────────────────────────────────────────────── */}
      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Branch parallelism</div>
        <input
          type="number"
          min={1}
          max={64}
          className="wf-form-input wf-loop-max-num"
          placeholder="inherit (default)"
          value={maxParallelism ?? ""}
          onChange={(e) => setMaxParallelism(e.target.value)}
        />
        <p className="wf-retry-hint">
          How many element-branches run at once. Empty inherits the system default; set <code>1</code> to force sequential branches (e.g. a rate-limited API).
        </p>
      </section>

      {/* ── Error handling ───────────────────────────────────────────────── */}
      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Error handling</div>
        <select
          className="wf-form-input"
          value={errorHandling}
          onChange={(e) => onConfigChange({ ...config, errorHandling: e.target.value })}
        >
          <option value="terminate">Terminate on error (default)</option>
          <option value="continue">Continue on error</option>
        </select>
        <p className="wf-retry-hint">
          {errorHandling === "continue"
            ? "A failing branch (with no error route of its own) records a failure marker in its slot — the map keeps going and reports how many branches failed."
            : "Any branch failure without its own error route fails the whole map."}
        </p>
      </section>

      {/* ── Result key ───────────────────────────────────────────────────── */}
      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Result key</div>
        <input
          className="wf-form-input"
          placeholder="results"
          value={resultKey}
          onChange={(e) => setResultKey(e.target.value)}
        />
        <p className="wf-retry-hint">
          The output key the collected array lands under. A downstream step reads it as <code>{"{{nodes."}{"<map>"}{".outputs."}{resultKey || "results"}{"}}"}</code>. Empty uses the default <code>results</code>.
        </p>
      </section>
    </>
  );
}
