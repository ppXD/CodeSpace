import type {
  NodeManifestDto,
  WorkflowActivationInput,
  WorkflowDefinition,
  WorkflowDetail,
} from "@/api/workflows";

/**
 * Project a workflow definition's Trigger nodes onto the activation rows the backend
 * stores (`workflow_activation`). Each Trigger node maps to exactly one activation; its
 * config is the matcher's filter (repos, labels, etc).
 *
 * <h3>Source of truth: node, not existing activation</h3>
 * The node's <c>config</c> wins. The trigger inspector edits node config; we project it
 * into the activation on save. The previous shape took config from the existing activation
 * row, which meant freshly-edited node config never reached the matcher (the bug this
 * function name shows up in via blame on the PR that fixed it).
 *
 * <h3>What we DO carry over from the existing activation row</h3>
 * The <c>enabled</c> flag — toggling a trigger off shouldn't get reset by a save. A new
 * trigger (no matching existing row) defaults to <c>enabled: true</c> so it actually fires.
 *
 * <h3>Matching</h3>
 * By <c>typeKey</c> only. Two trigger nodes of the same kind (e.g. two `trigger.pr.opened`)
 * both get rows; the first matching existing row supplies the enabled flag (a deliberate
 * approximation — a workflow with two same-typeKey triggers is rare, and the backend treats
 * them independently).
 *
 * <h3>Classification</h3>
 * Trigger-ness via the manifest's `kind === "Trigger"`, NOT a hardcoded `trigger.*` prefix.
 * The manifest's declared kind is the single source of truth and covers plugin authors who
 * name their trigger `schedule.cron` or `slack.mention`.
 *
 * <h3>Manual triggers produce no activation</h3>
 * An on-demand trigger (manifest `isManual === true`, e.g. `trigger.manual`) starts runs by
 * hand / API — it subscribes to no event source, so there is nothing for a matcher to match.
 * We exclude it here: emitting an activation row for it would be a meaningless "subscription"
 * that never fires and would pollute the workflow's activation list. Driven by the manifest
 * flag, not a hardcoded type key, so future on-demand triggers get the same treatment for free.
 */
export function deriveActivations(
  definition: WorkflowDefinition,
  existing: WorkflowDetail["activations"],
  manifestByType: Map<string, NodeManifestDto>,
): WorkflowActivationInput[] {
  const triggerNodes = definition.nodes.filter((n) => {
    const manifest = manifestByType.get(n.typeKey);
    return manifest?.kind === "Trigger" && !manifest.isManual;
  });
  return triggerNodes.map((n) => {
    const existingMatch = existing.find((a) => a.typeKey === n.typeKey);
    return {
      typeKey: n.typeKey,
      enabled: existingMatch?.enabled ?? true,
      config: n.config ?? {},
    };
  });
}
