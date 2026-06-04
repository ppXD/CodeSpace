import { SchemaForm } from "@/components/workflows/SchemaForm";
import type { ScopeSuggestion } from "@/components/workflows/scope-introspection";

/**
 * Custom Inputs editor for `chat.post_message`. Replaces the confusing "fill actions / fill form /
 * fill component — all three simultaneously visible" layout with:
 *
 *   conversationId + body        (always shown)
 *   ── Interaction type ──────────────────
 *   [None] [Buttons] [Form] …    (x-interactionField options from the manifest)
 *   sub-editor for selected type
 *   ──────────────────────────────────────
 *   allowedResponderUserIds + component  (always shown, component in Advanced)
 *
 * Generic: the type picker options are DATA-DRIVEN — discovered by reading `x-interactionField: true`
 * properties from the `inputSchema`. Adding a new interaction kind to the backend only requires:
 *   1. A new `IInteractionComponentFactory` (backend registry — already generic).
 *   2. A new `x-interactionField: true, x-interactionLabel: "…"` property in the InputSchema.
 *   3. Nothing here changes.
 *
 * Stored values: still the same `actions` / `form` keys — fully non-breaking.
 */
export function PostMessageInputsEditor({ inputs, onChange, variableSuggestions, inputSchema }: {
  inputs: Record<string, unknown>;
  onChange: (next: Record<string, unknown>) => void;
  variableSuggestions?: ScopeSuggestion[];
  inputSchema: unknown;
}) {
  const parsed = parseInputSchema(inputSchema);

  // Derive the active interaction kind: the first interaction-field key that is present in inputs.
  // "form" wins over "actions" (matches the backend precedence) to correctly reflect existing definitions.
  const activeKind = parsed.interactionFields.find(f => inputs[f.key] != null)?.key ?? "none";

  const switchKind = (next: string) => {
    // Remove all interaction fields, then seed the new kind's default.
    const without: Record<string, unknown> = { ...inputs };
    for (const f of parsed.interactionFields) delete without[f.key];
    if (next === "none") { onChange(without); return; }
    const seed = INTERACTION_DEFAULTS[next] ?? {};
    onChange({ ...without, ...seed });
  };

  const updateInteraction = (partial: Record<string, unknown>) => onChange({ ...inputs, ...partial });

  return (
    <div className="wf-form">
      {/* Always-visible message fields (conversationId + body). */}
      <SchemaForm
        schema={parsed.upperSchema}
        value={inputs}
        onChange={(partial) => onChange({ ...inputs, ...partial })}
        templateHint
        variableSuggestions={variableSuggestions}
      />

      {/* ── Interaction type picker ────────────────────────────────────────────── */}
      <div className="wf-interaction-picker">
        <span className="wf-form-label">Interaction</span>
        <div className="wf-interaction-options" role="group" aria-label="Interaction type">
          <button
            type="button"
            className="wf-interaction-opt"
            data-active={activeKind === "none" || undefined}
            onClick={() => switchKind("none")}
          >
            None
          </button>
          {parsed.interactionFields.map(f => (
            <button
              key={f.key}
              type="button"
              className="wf-interaction-opt"
              data-active={activeKind === f.key || undefined}
              onClick={() => switchKind(f.key)}
            >
              {f.label}
            </button>
          ))}
        </div>
        <span className="wf-form-help">
          {activeKind === "none" ? "Plain message — no interaction." : ""}
        </span>
      </div>

      {/* ── Sub-editor for the selected interaction type ──────────────────────── */}
      {activeKind !== "none" && (() => {
        const field = parsed.interactionFields.find(f => f.key === activeKind);
        if (!field) return null;
        return (
          <div className="wf-interaction-body">
            <SchemaForm
              schema={field.wrappedSchema}
              value={{ [field.key]: inputs[field.key] }}
              onChange={(partial) => updateInteraction(partial)}
              templateHint
              variableSuggestions={variableSuggestions}
            />
          </div>
        );
      })()}

      {/* Always-visible lower fields (allowedResponderUserIds + component in Advanced). */}
      <SchemaForm
        schema={parsed.lowerSchema}
        value={inputs}
        onChange={(partial) => onChange({ ...inputs, ...partial })}
        variableSuggestions={variableSuggestions}
      />
    </div>
  );
}

// ─── Defaults seeded when switching to a new kind ───────────────────────────────────────────────

const INTERACTION_DEFAULTS: Record<string, Record<string, unknown>> = {
  actions: { actions: [] },
  form: { form: { fields: { type: "object", properties: {} }, submitLabel: "Submit" } },
};

// ─── Schema parsing ─────────────────────────────────────────────────────────────────────────────

interface InteractionField { key: string; label: string; wrappedSchema: unknown }
interface ParsedSchema {
  upperSchema: unknown;
  lowerSchema: unknown;
  interactionFields: InteractionField[];
}

/**
 * Parses the node's `inputSchema` to discover:
 *   - `upperSchema`: non-interaction fields rendered above the type picker (conversationId, body).
 *   - `lowerSchema`: non-interaction fields rendered below the type picker (allowedResponderUserIds,
 *     component). These are the fields that come AFTER the interaction fields in property declaration order.
 *   - `interactionFields`: the `x-interactionField: true` properties, in declaration order, with their
 *     human label and a "wrapped" schema `{ type:"object", properties: { <key>: <propSchema> } }` ready
 *     for SchemaForm to render just that one field.
 *
 * Splits at the FIRST interaction field: everything before it is `upper`, everything after it
 * (and not itself an interaction field) is `lower`. This matches the visual order in the inspector.
 */
function parseInputSchema(inputSchema: unknown): ParsedSchema {
  const empty = { upperSchema: { type: "object", properties: {} }, lowerSchema: { type: "object", properties: {} }, interactionFields: [] };

  if (typeof inputSchema !== "object" || inputSchema == null) return empty;
  const s = inputSchema as { type?: string; properties?: Record<string, Record<string, unknown>>; required?: string[] };
  if (!s.properties) return empty;

  const required = new Set(s.required ?? []);
  const entries = Object.entries(s.properties);

  const interactionKeys = new Set(entries.filter(([, p]) => p["x-interactionField"]).map(([k]) => k));
  const firstInteractionIdx = entries.findIndex(([k]) => interactionKeys.has(k));

  const upperEntries = firstInteractionIdx < 0 ? entries : entries.slice(0, firstInteractionIdx);
  const lowerEntries = entries.filter(([k]) => !interactionKeys.has(k) && !upperEntries.some(([uk]) => uk === k));

  const buildSchema = (ents: [string, unknown][]) => ({
    type: "object" as const,
    properties: Object.fromEntries(ents),
    required: ents.map(([k]) => k).filter(k => required.has(k)),
  });

  const interactionFields: InteractionField[] = entries
    .filter(([, p]) => p["x-interactionField"])
    .map(([key, prop]) => ({
      key,
      label: (prop["x-interactionLabel"] as string | undefined) ?? key,
      wrappedSchema: { type: "object", properties: { [key]: prop } },
    }));

  return { upperSchema: buildSchema(upperEntries), lowerSchema: buildSchema(lowerEntries), interactionFields };
}
