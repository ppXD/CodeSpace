import { composeIntent } from "./intentCompose";
import { useIntentEntityResolver } from "./useIntentEntityResolver";

interface IntentLineProps {
  configSchema: unknown;
  inputSchema: unknown;
  config: Record<string, unknown>;
  inputs: Record<string, unknown>;
}

/**
 * The always-first, plain-language summary of what a node WILL DO given its current config — rendered at
 * the top of the inspector body. Opt-in: a node declares an `x-intent` template on its ConfigSchema root;
 * a node without one renders nothing (and never mounts the entity resolver).
 *
 * Split into a cheap gate (no hooks) + a resolved child so quiet nodes pay zero cost while the resolved
 * child can call the entity-resolver hook unconditionally.
 */
export function IntentLine({ configSchema, inputSchema, config, inputs }: IntentLineProps) {
  const template = (configSchema && typeof configSchema === "object"
    ? (configSchema as { "x-intent"?: unknown })["x-intent"]
    : undefined);
  if (typeof template !== "string" || template.trim() === "") return null;
  return <IntentLineResolved configSchema={configSchema} inputSchema={inputSchema} config={config} inputs={inputs} />;
}

function IntentLineResolved({ configSchema, inputSchema, config, inputs }: IntentLineProps) {
  const resolver = useIntentEntityResolver();
  const segments = composeIntent(configSchema, inputSchema, config, inputs, resolver);
  if (!segments || segments.length === 0) return null;

  return (
    <p className="wf-intent">
      <span className="wf-intent-icon" aria-hidden="true">✦</span>
      <span className="wf-intent-text">
        {segments.map((seg, i) => {
          switch (seg.type) {
            case "entity": return <span key={i} className="wf-intent-entity">{seg.name}</span>;
            case "ref": return <span key={i} className="wf-intent-ref">{seg.label}</span>;
            case "prompt": return <span key={i} className="wf-intent-prompt">{seg.text}</span>;
            default: return <span key={i}>{seg.text}</span>;
          }
        })}
      </span>
    </p>
  );
}
