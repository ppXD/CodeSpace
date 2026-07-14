import { describe, expect, it } from "vitest";

import type { NodeStatus, WorkflowDefinition } from "@/api/workflows";
import { ERROR_HANDLE } from "@/lib/workflowErrorRoute";
import { definitionToRunEdges } from "./RunCanvas";
import { CATCH_HANDLE } from "./workflowContainers";

// A run graph whose source succeeded, with a normal taken hop, a Skipped (dead) branch, an error edge, and a
// catch edge — enough to exercise the run-state class composition and the terminal-run animation gate.
const definition: WorkflowDefinition = {
  schemaVersion: 1,
  nodes: [],
  edges: [
    { from: "a", to: "b" },                                 // normal → taken
    { from: "a", to: "c" },                                 // normal → dead (target Skipped)
    { from: "a", to: "e", sourceHandle: ERROR_HANDLE },     // error route
    { from: "a", to: "f", sourceHandle: CATCH_HANDLE },     // try catch route
  ],
};

const statuses = new Map<string, NodeStatus>([
  ["a", "Success"], ["b", "Success"], ["c", "Skipped"], ["e", "Success"], ["f", "Success"],
]);

const byTarget = (runActive: boolean) => new Map(definitionToRunEdges(definition, statuses, runActive).map((e) => [e.target, e]));

describe("definitionToRunEdges", () => {
  it("composes the run-state class with the taken/dead grammar", () => {
    const edges = byTarget(true);
    expect(edges.get("b")?.className).toBe("wf-rf-edge-taken");
    expect(edges.get("c")?.className).toBe("wf-rf-edge-dead");
    // The route class (error/catch) composes with the state class (here both targets are Success → taken).
    expect(edges.get("e")?.className).toBe("wf-rf-edge-error wf-rf-edge-taken");
    expect(edges.get("f")?.className).toBe("wf-rf-edge-catch wf-rf-edge-taken");
  });

  it("animates error/catch edges only while the run is live", () => {
    const live = byTarget(true);
    expect(live.get("e")?.animated).toBe(true);
    expect(live.get("f")?.animated).toBe(true);

    // The bug fix: a terminal run's error/catch edge is static, not marching. Class is kept for styling.
    const terminal = byTarget(false);
    expect(terminal.get("e")?.animated).toBeUndefined();
    expect(terminal.get("f")?.animated).toBeUndefined();
    expect(terminal.get("e")?.className).toBe("wf-rf-edge-error wf-rf-edge-taken");
  });
});
