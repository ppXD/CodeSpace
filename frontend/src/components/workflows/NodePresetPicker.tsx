import type { NodePreset } from "@/api/workflows";

/**
 * "Start from a template" chooser for a node that declares presets in its manifest. Generic — any node
 * with presets gets it, no per-type code here. Picking a preset REPLACES the node's config + inputs with
 * the preset's (a friendly surface over the raw schemas, so an author picks an intent like "Quorum review"
 * instead of assembling per-action flags by hand).
 *
 * Collapsed by default (a deliberate expand-then-click), so it can't clobber real work by accident — and
 * it stays in the warm in-app theme rather than using a native confirm dialog.
 */
export function NodePresetPicker({ presets, onApply }: { presets: NodePreset[]; onApply: (preset: NodePreset) => void }) {
  if (presets.length === 0) return null;

  return (
    <details className="wf-inspector-presets">
      <summary className="wf-inspector-presets-summary">Start from a template</summary>
      <div className="wf-inspector-presets-body">
        {presets.map(p => (
          <button key={p.id} type="button" className="wf-inspector-preset" onClick={() => onApply(p)}>
            <span className="wf-inspector-preset-label">{p.label}</span>
            {p.description && <span className="wf-inspector-preset-desc">{p.description}</span>}
          </button>
        ))}
      </div>
    </details>
  );
}
