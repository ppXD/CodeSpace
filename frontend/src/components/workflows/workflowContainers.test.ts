import { describe, expect, it } from "vitest";

import { bodyStartTypeKey, CATCH_HANDLE, isBodyStartTypeKey, isContainerKind, sameContainerScope } from "./workflowContainers";

describe("workflowContainers", () => {
  it("pins the catch handle wire value (mirrors backend WorkflowHandles.Catch)", () => {
    expect(CATCH_HANDLE).toBe("catch");
  });

  it("treats loop, try and map as containers, nothing else", () => {
    expect(isContainerKind("Loop")).toBe(true);
    expect(isContainerKind("Try")).toBe(true);
    expect(isContainerKind("Map")).toBe(true);
    expect(isContainerKind("Regular")).toBe(false);
    expect(isContainerKind("Trigger")).toBe(false);
    expect(isContainerKind("Terminal")).toBe(false);
    expect(isContainerKind(undefined)).toBe(false);
    expect(isContainerKind(null)).toBe(false);
  });

  it("maps a container typeKey to its body-start marker", () => {
    expect(bodyStartTypeKey("flow.loop")).toBe("flow.loop_start");
    expect(bodyStartTypeKey("flow.try")).toBe("flow.try_start");
    expect(bodyStartTypeKey("flow.map")).toBe("flow.map_start");
    expect(bodyStartTypeKey("http.request")).toBeNull();
    expect(bodyStartTypeKey("flow.loop_start")).toBeNull(); // a marker is not itself a container
    expect(bodyStartTypeKey("flow.map_start")).toBeNull();
  });

  it("recognises all three body-start markers (and only those)", () => {
    expect(isBodyStartTypeKey("flow.loop_start")).toBe(true);
    expect(isBodyStartTypeKey("flow.try_start")).toBe(true);
    expect(isBodyStartTypeKey("flow.map_start")).toBe(true);
    expect(isBodyStartTypeKey("flow.loop")).toBe(false);
    expect(isBodyStartTypeKey("flow.try")).toBe(false);
    expect(isBodyStartTypeKey("flow.map")).toBe(false);
    expect(isBodyStartTypeKey(undefined)).toBe(false);
  });
});

/**
 * The cross-container connection rule — the EXACT mirror of the backend
 * DefinitionValidator.CheckNoEdgeCrossesContainerBoundary: an edge is valid iff both endpoints share
 * the same container owner (ownerByNodeId[from] === ownerByNodeId[to]). The cases below mirror the
 * backend's documented boundary exceptions, all of which are same-owner edges that fall out of the
 * single rule (no special casing): the container node ↔ outside sibling, body-start ↔ body node, an
 * in-body error edge, and a try catch edge sourced from the parent-level node.
 *
 * Topology used:
 *   top1, top2          — top-level nodes        (owner: undefined)
 *   mapNode             — a flow.map container    (owner: undefined; it's a top-level node)
 *   mapStart, bodyA     — inside the map body     (owner: "mapNode")
 *   loopNode            — a flow.loop container   (owner: undefined)
 *   loopBody            — inside the loop body    (owner: "loopNode")
 */
describe("sameContainerScope — mirrors the backend cross-container boundary rule", () => {
  const owner = new Map<string, string | null | undefined>([
    ["top1", undefined],
    ["top2", undefined],
    ["mapNode", undefined],     // the container node itself lives at top level (same scope as its siblings)
    ["mapStart", "mapNode"],
    ["bodyA", "mapNode"],
    ["loopNode", undefined],
    ["loopBody", "loopNode"],
  ]);

  it("allows a valid same-scope edge — two top-level nodes", () => {
    expect(sameContainerScope(owner, "top1", "top2")).toBe(true);
  });

  it("allows the container node wiring to/from an outside sibling (the container connects through ITSELF)", () => {
    expect(sameContainerScope(owner, "top1", "mapNode")).toBe(true);   // outside → map node
    expect(sameContainerScope(owner, "mapNode", "top2")).toBe(true);   // map node → outside
  });

  it("allows an in-body edge — body-start to a body node, both owned by the map", () => {
    expect(sameContainerScope(owner, "mapStart", "bodyA")).toBe(true);
  });

  it("allows an in-body error edge (same owner) — error edges are just edges", () => {
    expect(sameContainerScope(owner, "bodyA", "mapStart")).toBe(true);
  });

  it("BLOCKS a crossing edge — an outside node wired directly to a body node", () => {
    expect(sameContainerScope(owner, "top1", "bodyA")).toBe(false);
  });

  it("BLOCKS a crossing edge — a body node wired directly to an outside node", () => {
    expect(sameContainerScope(owner, "bodyA", "top2")).toBe(false);
  });

  it("BLOCKS a body node of one container wired to a body node of another container", () => {
    expect(sameContainerScope(owner, "bodyA", "loopBody")).toBe(false);
  });

  it("allows a same-scope edge between two body nodes of the SAME container", () => {
    const both = new Map<string, string | null | undefined>([["b1", "mapNode"], ["b2", "mapNode"]]);
    expect(sameContainerScope(both, "b1", "b2")).toBe(true);
  });

  it("treats null and undefined owners as the same (both top-level)", () => {
    const mixed = new Map<string, string | null | undefined>([["a", null], ["b", undefined]]);
    expect(sameContainerScope(mixed, "a", "b")).toBe(true);
  });

  it("allows an unknown endpoint (can't tell) — mirrors the backend's continue for an unknown node", () => {
    expect(sameContainerScope(owner, "top1", "ghost")).toBe(true);
    expect(sameContainerScope(owner, "ghost", "bodyA")).toBe(true);
  });
});
