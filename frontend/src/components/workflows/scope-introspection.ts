import type { VariableSummary } from "@/api/variables";
import type {
  NodeManifestDto,
  SystemVariableDto,
  WorkflowDefinition,
} from "@/api/workflows";

/**
 * Computes the set of variables ACTUALLY in scope at a specific node's position in the
 * graph. The output drives the autocomplete picker AND the "Available variables here"
 * inspector card so the operator never has to guess which {{ref}} paths are legal.
 *
 * Source of truth — exactly mirrors what the backend VariableResolver walks:
 *
 *   trigger.*    — payload keys (we don't know them statically; show one entry per
 *                  declared trigger output if the trigger node's manifest declares one,
 *                  else a single generic "trigger.<payload>" hint)
 *   nodes.X.outputs.Y — every upstream node's declared OutputSchema properties
 *   wf.X         — every workflow variable declared in Definition.variables
 *   input.X      — every workflow input declared in Definition.inputs
 *   team.X       — team-scoped variables. We don't fetch the actual names here (managed on
 *                  the Team Settings page); the picker shows a generic "team.<name>" hint
 *                  so authors learn the scope exists.
 *   sys.X        — engine-injected globals (workflow_id, source_type, started_at, …).
 *                  Fixed set sourced from the backend (SystemScopeKeys.Descriptors via
 *                  GET /api/workflows/system-variables) and passed in as `systemVariables`.
 *   iteration.*  — only when the current node is downstream of (or IS) a flow.iterate
 *                  node, in which case item/index are valid
 *
 * Returns a flat list of suggestions ordered by relevance: upstream node outputs first
 * (most likely usage), then wf/input (workflow-scoped), then trigger, then team/iteration/sys.
 */

export interface ScopeSuggestion {
  /** What the operator types after {{. E.g. "nodes.fetch.outputs.files" or "wf.maxRetries". */
  path: string;
  /** Display label for the dropdown row. */
  label: string;
  /** Source category (drives icon + group header). */
  category: "node" | "wf" | "input" | "trigger" | "team" | "iteration" | "sys" | "project";
  /** Optional type hint shown after the label (e.g. "string", "array"). */
  type?: string;
  /** Optional one-line description. */
  description?: string;
}

interface IntrospectArgs {
  definition: WorkflowDefinition;
  currentNodeId: string | null;
  manifestByType: Map<string, NodeManifestDto>;
  /** Real workflow-scoped variables fetched from /api/workflows/{id}/variables. */
  workflowVariables?: ReadonlyArray<VariableSummary>;
  /** Real team-scoped variables fetched from /api/team-variables. */
  teamVariables?: ReadonlyArray<VariableSummary>;
  /** Engine-injected sys.* variables fetched from /api/workflows/system-variables. */
  systemVariables?: ReadonlyArray<SystemVariableDto>;
  /** Project-scoped variables, grouped by project slug. The backend resolver
   *  walks `project.{slug}.{name}` — passing one entry per project in the team
   *  lets the picker emit the full set of valid refs across projects, not just
   *  the workflow's own project (a workflow can legally reference any project's
   *  variables in the same team). Each entry's `variables` may be empty, in
   *  which case we still emit a `project.{slug}.<name>` placeholder so the
   *  scope head is discoverable. */
  projectVariables?: ReadonlyArray<{ slug: string; variables: ReadonlyArray<VariableSummary> }>;
}

export function introspectScope({ definition, currentNodeId, manifestByType, workflowVariables, teamVariables, systemVariables, projectVariables }: IntrospectArgs): ScopeSuggestion[] {
  const suggestions: ScopeSuggestion[] = [];

  // 1. Upstream node outputs — only nodes that REACH the current node along directed edges.
  const upstream = currentNodeId ? collectUpstream(definition, currentNodeId) : new Set<string>();
  for (const nodeId of upstream) {
    const node = definition.nodes.find((n) => n.id === nodeId);
    if (!node) continue;
    const manifest = manifestByType.get(node.typeKey);
    const outputKeys = manifest ? extractOutputKeys(manifest) : [];

    if (outputKeys.length === 0) {
      // Node has no typed outputs declared — still show a generic placeholder so the
      // operator can hand-write a path if they know what the node emits.
      suggestions.push({
        path: `nodes.${nodeId}.outputs`,
        label: `nodes.${nodeId}.outputs`,
        category: "node",
        description: `${manifest?.displayName ?? node.typeKey} — outputs not typed`,
      });
      continue;
    }

    for (const key of outputKeys) {
      suggestions.push({
        path: `nodes.${nodeId}.outputs.${key.name}`,
        label: `nodes.${nodeId}.outputs.${key.name}`,
        category: "node",
        type: key.type,
        description: `From "${node.label || manifest?.displayName || node.typeKey}"`,
      });
    }
  }

  // 2. Workflow variables — live in the unified `variable` table (scope=Workflow); one
  //    autocomplete entry per real row. Falls back to a generic catch-all when the hook
  //    hasn't loaded yet OR no variables exist (so authors still see the scope head).
  if (workflowVariables && workflowVariables.length > 0) {
    for (const v of workflowVariables) {
      suggestions.push({
        path: `wf.${v.name}`,
        label: `wf.${v.name}`,
        category: "wf",
        type: v.valueType === "Secret" ? "secret" : v.valueType.toLowerCase(),
        description: v.description ?? (v.valueType === "Secret" ? "Workflow secret (re-resolved at run time)" : "Workflow variable"),
      });
    }
  } else {
    suggestions.push({
      path: "wf.",
      label: "wf.<name>",
      category: "wf",
      description: "Workflow variable — add one via the Variables panel.",
    });
  }

  // 3. Workflow inputs.
  for (const v of definition.inputs ?? []) {
    suggestions.push({
      path: `input.${v.name}`,
      label: `input.${v.name}`,
      category: "input",
      type: extractSchemaType(v.schema),
      description: v.description ?? v.label ?? (v.required ? "Required input" : "Optional input"),
    });
  }

  // 4. Trigger payload — fields declared by the trigger node's OutputSchema (when typed),
  // otherwise a generic catch-all hint.
  const triggerNode = definition.nodes.find((n) => manifestByType.get(n.typeKey)?.kind === "Trigger");
  if (triggerNode) {
    const triggerManifest = manifestByType.get(triggerNode.typeKey);
    const triggerKeys = triggerManifest ? extractOutputKeys(triggerManifest) : [];

    if (triggerKeys.length === 0) {
      suggestions.push({
        path: "trigger.",
        label: "trigger.<key>",
        category: "trigger",
        description: "Trigger payload — fields depend on the event source",
      });
    } else {
      for (const key of triggerKeys) {
        suggestions.push({
          path: `trigger.${key.name}`,
          label: `trigger.${key.name}`,
          category: "trigger",
          type: key.type,
          description: `Trigger payload field`,
        });
      }
    }
  }

  // 5. System variables — engine-injected per-run context, always available. Sourced from
  // the backend (SystemScopeKeys.Descriptors). When the hook hasn't resolved yet we render
  // nothing rather than a stale placeholder; the picker simply gains the sys.* group as
  // soon as the cached query lands.
  for (const sv of systemVariables ?? []) {
    suggestions.push({
      path: `sys.${sv.key}`,
      label: `sys.${sv.key}`,
      category: "sys",
      type: sv.type,
      description: sv.description,
    });
  }

  // 6. Team variables — same scope head as wf.* but resolved from scope=Team rows. One
  //    entry per real variable (so {{team.test}} autocompletes the moment it's saved via
  //    the Team panel). Generic placeholder kept for the empty-list case.
  if (teamVariables && teamVariables.length > 0) {
    for (const v of teamVariables) {
      suggestions.push({
        path: `team.${v.name}`,
        label: `team.${v.name}`,
        category: "team",
        type: v.valueType === "Secret" ? "secret" : v.valueType.toLowerCase(),
        description: v.description ?? (v.valueType === "Secret" ? "Team secret (re-resolved at run time)" : "Team variable"),
      });
    }
  } else {
    suggestions.push({
      path: "team.",
      label: "team.<name>",
      category: "team",
      description: "Team variable — add one via the Team panel.",
    });
  }

  // 6. Iteration scope — only meaningful when the current node sits downstream of a
  //    flow.iterate. We test this by checking whether ANY upstream node is iterate.
  const currentIsInIterationContext = Array.from(upstream).some((id) => {
    const n = definition.nodes.find((x) => x.id === id);
    return n?.typeKey === "flow.iterate";
  }) || (currentNodeId != null && definition.nodes.find((n) => n.id === currentNodeId)?.typeKey === "flow.iterate");

  if (currentIsInIterationContext) {
    suggestions.push(
      { path: "item",  label: "item",  category: "iteration", description: "Current iteration element" },
      { path: "index", label: "index", category: "iteration", type: "integer", description: "0-based iteration index" },
    );
  }

  // 7. Project variables — backend resolves `project.{slug}.{name}` against every
  //    project in the active team. Emit one suggestion per REAL variable across
  //    all projects; projects with no variables are skipped entirely. (We
  //    initially emitted a `project.{slug}.<name>` placeholder per empty project
  //    for discoverability — that turned out to clutter the picker with
  //    non-selectable rows for every empty project in the team, so we dropped
  //    them. Empty projects simply contribute nothing.)
  for (const proj of projectVariables ?? []) {
    for (const v of proj.variables) {
      suggestions.push({
        path: `project.${proj.slug}.${v.name}`,
        label: `project.${proj.slug}.${v.name}`,
        category: "project",
        type: v.valueType === "Secret" ? "secret" : v.valueType.toLowerCase(),
        description: v.description ?? (v.valueType === "Secret" ? "Project secret (re-resolved at run time)" : "Project variable"),
      });
    }
  }

  return suggestions;
}

/** BFS backwards from a node along incoming edges. Returns ids of every ancestor. */
function collectUpstream(definition: WorkflowDefinition, nodeId: string): Set<string> {
  const upstream = new Set<string>();
  const stack: string[] = [];
  for (const e of definition.edges) if (e.to === nodeId) stack.push(e.from);

  while (stack.length > 0) {
    const current = stack.pop()!;
    if (upstream.has(current)) continue;
    upstream.add(current);
    for (const e of definition.edges) if (e.to === current) stack.push(e.from);
  }
  return upstream;
}

interface OutputKey { name: string; type?: string; }

function extractOutputKeys(manifest: NodeManifestDto): OutputKey[] {
  const schema = manifest.outputSchema as { properties?: Record<string, { type?: string | string[] }> } | undefined;
  if (!schema?.properties) return [];
  return Object.entries(schema.properties).map(([name, prop]) => ({
    name,
    type: Array.isArray(prop.type) ? prop.type.join("|") : prop.type,
  }));
}

function extractSchemaType(schema: unknown): string | undefined {
  if (typeof schema !== "object" || schema == null) return undefined;
  const t = (schema as { type?: string | string[] }).type;
  if (!t) return undefined;
  return Array.isArray(t) ? t.join("|") : t;
}
