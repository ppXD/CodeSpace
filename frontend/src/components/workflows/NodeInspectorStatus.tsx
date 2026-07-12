import { Ic } from "@/_imported/ai-code-space/icons";

import { nodeReadiness } from "./nodeReadiness";

export interface NodeInspectorStatusProps {
  configSchema: unknown;
  config: unknown;
  inputSchema: unknown;
  inputs: unknown;
}

/**
 * The inspector's at-a-glance readiness line: does this node still need something before it can run?
 * Driven entirely by the manifest's required fields (see nodeReadiness) — a node with nothing required
 * shows no line at all, so simple nodes stay uncluttered. This is the first "is it ready?" signal the
 * author gets without hunting for the `*` on individual fields.
 */
export function NodeInspectorStatus({ configSchema, config, inputSchema, inputs }: NodeInspectorStatusProps) {
  const { requiredCount, missing } = nodeReadiness(configSchema, config, inputSchema, inputs);

  if (requiredCount === 0) return null;

  if (missing.length === 0) {
    return (
      <div className="wf-inspector-status wf-inspector-status-ready">
        <Ic.Check size={13} />
        <span>Ready to run</span>
      </div>
    );
  }

  return (
    <div className="wf-inspector-status wf-inspector-status-missing">
      <Ic.Triangle size={13} />
      <span>
        Needs{" "}
        {missing.map((f, i) => (
          <span key={f.key}>
            {i > 0 && ", "}
            <b>{f.label}</b>
          </span>
        ))}
      </span>
    </div>
  );
}
