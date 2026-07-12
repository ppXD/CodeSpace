import { describe, expect, it } from "vitest";

import type { ScopeSuggestion } from "./scope-introspection";
import { ancestorIds, buildSuggestionTree, flattenVisible, friendlyType, typeFits, type SuggestionTreeNode } from "./suggestionTree";

const node = (path: string, label: string, type?: string): ScopeSuggestion => ({ path, label, category: "node", type, description: path });
const iter = (path: string, label: string, type?: string): ScopeSuggestion => ({ path, label, category: "iteration", type, description: path });

const child = (n: SuggestionTreeNode, label: string) => n.children.find((c) => c.label === label);

describe("buildSuggestionTree", () => {
  it("groups node outputs by source node, drilling fields underneath", () => {
    const tree = buildSuggestionTree([
      node("nodes.plan.outputs.items", "Create plan → items", "array"),
      node("nodes.plan.outputs.items[0].instruction", "Create plan → items[0].instruction", "string"),
      node("nodes.plan.outputs.items[0].acceptance", "Create plan → items[0].acceptance", "object"),
      node("nodes.plan.outputs.items[0].acceptance.command", "Create plan → items[0].acceptance.command", "array"),
      node("nodes.plan.outputs.executionNeeded", "Create plan → executionNeeded", "boolean"),
    ]);

    expect(tree).toHaveLength(1);
    expect(tree[0].category).toBe("node");

    const source = tree[0].roots[0];
    expect(source.label).toBe("Create plan");          // the node name is the branch
    expect(source.suggestion).toBeUndefined();          // a node itself isn't bindable, only its fields

    const items = child(source, "Items")!;
    expect(items.typeHint).toBe("List");                // whole-array bind
    expect(items.suggestion?.path).toBe("nodes.plan.outputs.items");

    const firstItem = child(items, "First item")!;      // the [0] index becomes an honest "First item" level
    expect(firstItem.suggestion).toBeUndefined();       // items[0] alone isn't offered (expand-only)

    const instruction = child(firstItem, "Instruction")!;
    expect(instruction.suggestion?.path).toBe("nodes.plan.outputs.items[0].instruction");
    expect(instruction.typeHint).toBe("Text");

    const command = child(child(firstItem, "Acceptance")!, "Command")!;
    expect(command.suggestion?.path).toBe("nodes.plan.outputs.items[0].acceptance.command");

    expect(child(source, "Execution Needed")?.typeHint).toBe("Yes/No");
  });

  it("a whole array is BOTH selectable and expandable", () => {
    const tree = buildSuggestionTree([
      node("nodes.list.outputs.pullRequests", "List PRs → pullRequests", "array"),
      node("nodes.list.outputs.pullRequests[0].number", "List PRs → pullRequests[0].number", "integer"),
    ]);
    const prs = child(tree[0].roots[0], "Pull Requests")!;
    expect(prs.suggestion?.path).toBe("nodes.list.outputs.pullRequests");   // selectable
    expect(prs.children.length).toBeGreaterThan(0);                          // expandable
  });

  it("builds the iteration Current-item tree from item.* paths", () => {
    const tree = buildSuggestionTree([
      iter("item", "Current item"),
      iter("index", "index", "integer"),
      iter("item.instruction", "Current item → instruction", "string"),
      iter("item.acceptance.command", "Current item → acceptance.command", "array"),
    ]);
    const roots = tree[0].roots;
    const item = roots.find((r) => r.label === "Item")!;
    expect(item.suggestion?.path).toBe("item");                    // bare {{item}} still selectable
    expect(child(item, "Instruction")?.suggestion?.path).toBe("item.instruction");
    expect(child(child(item, "Acceptance")!, "Command")?.suggestion?.path).toBe("item.acceptance.command");
    expect(roots.find((r) => r.label === "Index")?.suggestion?.path).toBe("index");
  });

  it("keeps flat scopes (wf/trigger) as shallow single-level roots", () => {
    const tree = buildSuggestionTree([
      { path: "wf.maxRetries", label: "wf.maxRetries", category: "wf", type: "integer" },
      { path: "trigger.number", label: "trigger.number", category: "trigger", type: "integer" },
    ]);
    expect(tree.map((g) => g.category)).toEqual(["wf", "trigger"]);
    expect(tree[0].roots[0].suggestion?.path).toBe("wf.maxRetries");
    expect(tree[0].roots[0].children).toHaveLength(0);
  });
});

describe("flattenVisible", () => {
  const tree = buildSuggestionTree([
    node("nodes.plan.outputs.items", "Create plan → items", "array"),
    node("nodes.plan.outputs.items[0].instruction", "Create plan → items[0].instruction", "string"),
  ]);
  const labelsAt = (expanded: Set<string>) => flattenVisible(tree, expanded).map((r) => r.node.label);

  it("shows only roots when nothing is expanded", () => {
    expect(labelsAt(new Set())).toEqual(["Create plan"]);
  });

  it("reveals children as ancestors expand, marking expandable rows", () => {
    const source = tree[0].roots[0];
    const items = child(source, "Items")!;
    const rows = flattenVisible(tree, new Set([source.id, items.id]));
    expect(rows.map((r) => r.node.label)).toEqual(["Create plan", "Items", "First item"]);
    expect(rows.find((r) => r.node.label === "Create plan")!.expandable).toBe(true);
    expect(rows.find((r) => r.node.label === "First item")!.depth).toBe(2);
  });
});

describe("ancestorIds", () => {
  it("lists every branch that must be open for a nested id to show", () => {
    expect(ancestorIds("node:plan/items/[0]/instruction")).toEqual([
      "node:plan",
      "node:plan/items",
      "node:plan/items/[0]",
    ]);
    expect(ancestorIds("wf.maxRetries")).toEqual([]);
  });
});

describe("typeFits — only selective field types single a value out", () => {
  it("an array field fits List values", () => {
    expect(typeFits("array", "array")).toBe(true);
    expect(typeFits("string", "array")).toBe(false);
  });
  it("a number field fits number/integer (either way), not strings", () => {
    expect(typeFits("integer", "number")).toBe(true);
    expect(typeFits("number", "integer")).toBe(true);
    expect(typeFits("string", "number")).toBe(false);
  });
  it("a boolean field fits booleans, tolerating a union", () => {
    expect(typeFits("boolean|null", "boolean")).toBe(true);
    expect(typeFits("integer", "boolean")).toBe(false);
  });
  it("a string field (or no expectation) singles nothing out — everything fits it", () => {
    expect(typeFits("integer", "string")).toBe(false);
    expect(typeFits("array", undefined)).toBe(false);
  });
});

describe("friendlyType", () => {
  it("maps json/scope types to plain words, collapsing unions", () => {
    expect(friendlyType("array")).toBe("List");
    expect(friendlyType("object")).toBe("Object");
    expect(friendlyType("string|null")).toBe("Text");
    expect(friendlyType("integer")).toBe("Number");
    expect(friendlyType("boolean")).toBe("Yes/No");
    expect(friendlyType(undefined)).toBeUndefined();
  });
});
