import type { ScopeSuggestion } from "./scope-introspection";

/**
 * Turns the picker's FLAT suggestion list (each a dotted `{{ref}}` path) into a collapsible TREE grouped by
 * source, so an author drills "Create plan → Items → First item → Instruction" instead of scanning a wall of
 * pre-expanded `items[0].instruction` rows. Pure + framework-free: the picker renders the tree and owns
 * expand state; this module only shapes the data and is exhaustively unit-tested.
 *
 * A tree node may be BOTH selectable (it carries a `suggestion` → inserting its `{{ref}}`) AND expandable (it
 * has children) — e.g. a whole array binds to a map while its fields drill for a scalar consumer.
 */
export type SuggestionCategory = ScopeSuggestion["category"];

export interface SuggestionTreeNode {
  /** Stable unique id (the accumulated segment chain) — React key + expand-state key. */
  id: string;
  /** Human label for THIS segment only (e.g. "Items", "First item", "Instruction"). */
  label: string;
  category: SuggestionCategory;
  /** Friendly type shown as a chip ("List" / "Object" / "Text" / "Number" / "Yes/No"), or undefined. */
  typeHint?: string;
  /** Present ⇒ this node inserts a {{ref}}; absent ⇒ a pure structural branch (expand-only). */
  suggestion?: ScopeSuggestion;
  children: SuggestionTreeNode[];
}

export interface SuggestionTreeGroup {
  category: SuggestionCategory;
  label: string;
  roots: SuggestionTreeNode[];
}

const CATEGORY_ORDER: SuggestionCategory[] = ["node", "wf", "input", "trigger", "iteration", "sys", "team", "project"];

const CATEGORY_LABEL: Record<SuggestionCategory, string> = {
  node: "Upstream node outputs",
  wf: "Workflow variables",
  input: "Workflow inputs",
  trigger: "Trigger payload",
  iteration: "Iteration",
  sys: "System variables",
  team: "Team variables",
  project: "Project variables",
};

/** JSON/scope type → the plain word an ordinary person reads. Unions ("string|null") collapse to the base. */
export function friendlyType(type?: string): string | undefined {
  if (!type) return undefined;
  const base = type.split("|")[0].trim();
  return { array: "List", object: "Object", string: "Text", integer: "Number", number: "Number", boolean: "Yes/No", secret: "Secret" }[base] ?? base;
}

function humanizeSegment(seg: string): string {
  const idx = seg.match(/^\[(\d+)\]$/);
  if (idx) return idx[1] === "0" ? "First item" : `Item ${Number(idx[1]) + 1}`;
  return seg
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ")
    .replace(/^\w/, (c) => c.toUpperCase());
}

/** Split a field path "items[0].acceptance.command" → ["items", "[0]", "acceptance", "command"] (indices are levels). */
function splitFieldPath(fieldPath: string): string[] {
  const out: string[] = [];
  for (const dotSeg of fieldPath.split(".")) {
    if (dotSeg === "") continue;
    const m = dotSeg.match(/^([A-Za-z_][A-Za-z0-9_]*)((?:\[\d+\])*)$/);
    if (!m) { out.push(dotSeg); continue; }
    out.push(m[1]);
    for (const bracket of m[2].match(/\[\d+\]/g) ?? []) out.push(bracket);
  }
  return out;
}

/** The source display name for a node suggestion — the label's "Name → …" head, else the bare label. */
function sourceName(label: string): string {
  const arrow = label.indexOf(" → ");
  return arrow >= 0 ? label.slice(0, arrow) : label;
}

/** Insert a suggestion into a roots list at the given segment chain, creating branch nodes as needed. */
function insertChain(roots: SuggestionTreeNode[], segments: string[], suggestion: ScopeSuggestion, idPrefix: string): void {
  let level = roots;
  let id = idPrefix;
  segments.forEach((seg, i) => {
    id = id ? `${id}/${seg}` : seg;
    let node = level.find((n) => n.label === humanizeSegment(seg) && n.id === id);
    if (!node) {
      node = { id, label: humanizeSegment(seg), category: suggestion.category, children: [] };
      level.push(node);
    }
    if (i === segments.length - 1) {
      node.suggestion = suggestion;
      node.typeHint = friendlyType(suggestion.type);
    } else if (!node.typeHint) {
      node.typeHint = seg.startsWith("[") ? undefined : "Object";
    }
    level = node.children;
  });
}

export function buildSuggestionTree(suggestions: ScopeSuggestion[]): SuggestionTreeGroup[] {
  const byCategory = new Map<SuggestionCategory, ScopeSuggestion[]>();
  for (const s of suggestions) (byCategory.get(s.category) ?? byCategory.set(s.category, []).get(s.category)!).push(s);

  const groups: SuggestionTreeGroup[] = [];
  for (const category of CATEGORY_ORDER) {
    const items = byCategory.get(category);
    if (!items || items.length === 0) continue;
    groups.push({ category, label: CATEGORY_LABEL[category], roots: buildRoots(category, items) });
  }
  return groups;
}

function buildRoots(category: SuggestionCategory, items: ScopeSuggestion[]): SuggestionTreeNode[] {
  // Node outputs get an extra grouping level: one branch per SOURCE node, its fields drilling underneath.
  if (category === "node") {
    const roots: SuggestionTreeNode[] = [];
    for (const s of items) {
      const m = s.path.match(/^nodes\.([A-Za-z0-9_-]+)\.outputs(?:\.(.+))?$/);
      const nodeId = m?.[1] ?? s.path;
      const nodeKey = `node:${nodeId}`;
      let branch = roots.find((r) => r.id === nodeKey);
      if (!branch) {
        branch = { id: nodeKey, label: sourceName(s.label), category, children: [] };
        roots.push(branch);
      }
      const fieldSegs = m?.[2] ? splitFieldPath(m[2]) : [];
      if (fieldSegs.length === 0) { branch.suggestion = s; branch.typeHint = friendlyType(s.type); }
      else insertChain(branch.children, fieldSegs, s, nodeKey);
    }
    return roots;
  }

  const roots: SuggestionTreeNode[] = [];
  for (const s of items) {
    const segs = splitFieldPath(s.path);
    // Iteration heads (item / index) ARE the roots. Every other scope's head (wf / trigger / sys …) is
    // already the group label, so strip it — no redundant "wf" branch above "Max retries".
    const chain = category === "iteration" ? segs : segs.slice(1);

    if (chain.length === 0) {
      // A head-only hint (e.g. the "wf." / "trigger." placeholder when no real variable exists yet) —
      // surface it as a single leaf carrying its own guidance label, not a stripped-to-nothing node.
      roots.push({ id: `${category}:${s.path}`, label: s.label, category, suggestion: s, children: [] });
      continue;
    }
    insertChain(roots, chain, s, category === "iteration" ? "" : category);
  }
  return roots;
}

export interface VisibleRow {
  node: SuggestionTreeNode;
  /** 0 = a source/root row directly under the group header; deeper = nested field. */
  depth: number;
  expandable: boolean;
  expanded: boolean;
  groupCategory: SuggestionCategory;
  groupLabel: string;
}

/** Flatten the tree to the rows the picker actually shows + navigates, honouring the expand set. Each row
 *  carries its group so the renderer can drop a header when the group changes. */
export function flattenVisible(groups: SuggestionTreeGroup[], expanded: Set<string>): VisibleRow[] {
  const rows: VisibleRow[] = [];
  const walk = (nodes: SuggestionTreeNode[], depth: number, g: SuggestionTreeGroup) => {
    for (const node of nodes) {
      const isExpandable = node.children.length > 0;
      const isExpanded = isExpandable && expanded.has(node.id);
      rows.push({ node, depth, expandable: isExpandable, expanded: isExpanded, groupCategory: g.category, groupLabel: g.label });
      if (isExpanded) walk(node.children, depth + 1, g);
    }
  };
  for (const g of groups) walk(g.roots, 0, g);
  return rows;
}

/** Every branch id in the tree — used to fully open the tree while a search filter is active. */
export function allBranchIds(groups: SuggestionTreeGroup[]): Set<string> {
  const set = new Set<string>();
  const walk = (nodes: SuggestionTreeNode[]) => {
    for (const n of nodes) if (n.children.length > 0) { set.add(n.id); walk(n.children); }
  };
  for (const g of groups) walk(g.roots);
  return set;
}

/** Ids of every branch that must be OPEN for the given leaf/branch id to be visible (all ancestor prefixes). */
export function ancestorIds(id: string): string[] {
  const parts = id.split("/");
  const out: string[] = [];
  for (let i = 1; i < parts.length; i++) out.push(parts.slice(0, i).join("/"));
  return out;
}
