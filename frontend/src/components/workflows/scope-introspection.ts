import type { VariableSummary } from "@/api/variables";
import type {
  NodeDefinition,
  NodeManifestDto,
  SystemVariableDto,
  WorkflowDefinition,
} from "@/api/workflows";
import { ERROR_HANDLE } from "@/lib/workflowErrorRoute";

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

    // For a flow.wait_action node, follow its token back to the chat.post_message it pairs with and surface
    // that post's configured action keys as the known values of `action` — so a downstream node can see
    // "one of: approve, reject" instead of an opaque string and wire its branch against a real key.
    const actionKeys = node.typeKey === "flow.wait_action" ? resolveWaitActionKeys(definition, node) : null;

    for (const key of outputKeys) {
      const showsActionKeys = actionKeys != null && key.name === "action";
      suggestions.push({
        path: `nodes.${nodeId}.outputs.${key.name}`,
        label: `nodes.${nodeId}.outputs.${key.name}`,
        category: "node",
        type: key.type,
        description: showsActionKeys
          ? `Clicked action — one of: ${actionKeys.join(", ")}`
          : `From "${node.label || manifest?.displayName || node.typeKey}"`,
      });
    }
  }

  // 1b. Error outputs — every node emits `error` ({ message, node }) when it fails. Offer it only
  //     where it's actually reachable: a node whose `error` edge leads (directly or down the chain)
  //     to the current node. So an error-handler discovers {{nodes.X.outputs.error.*}} while a
  //     success-path node isn't cluttered with paths that would always resolve null there.
  for (const sourceId of collectErrorSources(definition, currentNodeId)) {
    const node = definition.nodes.find((n) => n.id === sourceId);
    const from = node?.label || manifestByType.get(node?.typeKey ?? "")?.displayName || sourceId;
    suggestions.push(
      {
        path: `nodes.${sourceId}.outputs.error.message`,
        label: `nodes.${sourceId}.outputs.error.message`,
        category: "node",
        type: "string",
        description: `Failure message from "${from}" (error branch)`,
      },
      {
        path: `nodes.${sourceId}.outputs.error.node`,
        label: `nodes.${sourceId}.outputs.error.node`,
        category: "node",
        type: "string",
        description: `The id of the node that failed`,
      },
    );
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

  // 6. Iteration scope — bare {{item}} / {{index}} resolve through NodeRunScope.Iteration, which the
  //    engine seeds in exactly two cases:
  //      a) flow.iterate exposes them to its DOWNSTREAM nodes (the per-item template scope), so a node
  //         downstream of (or being) a flow.iterate is in context.
  //      b) flow.map seeds a FRESH {item, index} per branch (BuildMapBranchScope), so every node in a
  //         map BODY (parented — directly or transitively — under a flow.map) is in context. Crucially
  //         this is the FIXED-name scope: a flow.loop body alone uses {{loop.<name>}} / {{loop.index}}
  //         and does NOT seed bare item/index — but a loop (or try) body nested INSIDE a map inherits
  //         the enclosing map's Iteration (BuildLoopScope passes outer.Iteration through), so walking the
  //         parentId chain to any flow.map ancestor is the precise test and keeps the loop-inside-map
  //         body covered while never leaking to an unrelated top-level node.
  const currentIsInIterationContext =
    Array.from(upstream).some((id) => definition.nodes.find((x) => x.id === id)?.typeKey === "flow.iterate") ||
    (currentNodeId != null && definition.nodes.find((n) => n.id === currentNodeId)?.typeKey === "flow.iterate") ||
    (currentNodeId != null && isInsideMapBody(definition, currentNodeId));

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

/**
 * Nodes whose `error` edge leads — directly or down the chain — to the given node, so their
 * `error` output is genuinely in scope there. A source X qualifies when its error edge points at
 * the current node OR at one of the current node's ancestors (the error branch flows through to it).
 */
function collectErrorSources(definition: WorkflowDefinition, currentNodeId: string | null): Set<string> {
  const sources = new Set<string>();
  if (!currentNodeId) return sources;

  const ancestors = collectUpstream(definition, currentNodeId);
  for (const e of definition.edges) {
    if (e.sourceHandle !== ERROR_HANDLE) continue;
    if (e.to === currentNodeId || ancestors.has(e.to)) sources.add(e.from);
  }
  return sources;
}

/**
 * True when a node lives inside a flow.map body — i.e. some ancestor up its `parentId` chain is a
 * flow.map node. Walking the whole chain (not just the direct parent) covers a node nested deeper —
 * inside a flow.loop / flow.try body that is itself inside a map — because that body inherits the
 * enclosing map's Iteration scope, so bare {{item}} / {{index}} genuinely resolve there. A guard
 * against a malformed parentId cycle caps the walk at the node count.
 */
function isInsideMapBody(definition: WorkflowDefinition, nodeId: string): boolean {
  const byId = new Map(definition.nodes.map((n) => [n.id, n]));

  let parent = byId.get(nodeId)?.parentId ?? null;
  const seen = new Set<string>();
  while (parent && !seen.has(parent)) {
    seen.add(parent);
    const p = byId.get(parent);
    if (p?.typeKey === "flow.map") return true;
    parent = p?.parentId ?? null;
  }
  return false;
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

/**
 * For a flow.wait_action node, follow its `token` input back to the chat.post_message that minted it and
 * return that post's configured action keys (its buttons). Lets the picker show the wait's `action` output
 * as "one of: approve, reject" rather than an opaque string, so the next node can branch on a real key.
 *
 * Returns null when the link can't be resolved STATICALLY — a literal token (no upstream post), a source
 * that isn't a chat.post_message, or an `actions` bound to an expression rather than a literal array.
 */
function resolveWaitActionKeys(definition: WorkflowDefinition, waitNode: NodeDefinition): string[] | null {
  const token = readRef(asObject(waitNode.inputs)?.token);
  const sourceId = token?.match(/nodes\.([A-Za-z0-9_-]+)\.outputs\.token/)?.[1];
  if (!sourceId) return null;

  const post = definition.nodes.find((n) => n.id === sourceId && n.typeKey === "chat.post_message");
  const actions = asObject(post?.inputs)?.actions;
  if (!Array.isArray(actions)) return null;

  const keys = actions
    .map((a) => (asObject(a)?.key))
    .filter((k): k is string => typeof k === "string" && k.length > 0);

  return keys.length > 0 ? keys : null;
}

/** Narrow an unknown to a plain object (not an array), else null. */
function asObject(value: unknown): Record<string, unknown> | null {
  return value != null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

/** Read a reference input in either wire form: a "{{ … }}" / bare string, or a { "$ref": "…" } object. */
function readRef(value: unknown): string | null {
  if (typeof value === "string") return value;
  const ref = asObject(value)?.$ref;
  return typeof ref === "string" ? ref : null;
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
