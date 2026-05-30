import type { Edge } from "@xyflow/react";
import { describe, expect, it } from "vitest";

import { ERROR_HANDLE, errorRouteTarget, setErrorRoute } from "./workflowErrorRoute";

const normal = (source: string, target: string): Edge => ({ id: `e-${source}-${target}`, source, target });
const error = (source: string, target: string): Edge => ({ id: `e-error-${source}-${target}`, source, target, sourceHandle: ERROR_HANDLE });

describe("workflowErrorRoute", () => {
  it("finds a node's error-route target, or null when it has none", () => {
    const edges = [normal("a", "ok"), error("a", "handler"), normal("b", "c")];

    expect(errorRouteTarget(edges, "a")).toBe("handler");
    expect(errorRouteTarget(edges, "b")).toBeNull();
    expect(errorRouteTarget(edges, "missing")).toBeNull();
  });

  it("adds an error edge (tagged + classed) without touching normal edges", () => {
    const edges = [normal("a", "ok")];

    const next = setErrorRoute(edges, "a", "handler");

    expect(next).toContain(edges[0]); // normal edge preserved
    const added = next.find((e) => e.source === "a" && e.sourceHandle === ERROR_HANDLE)!;
    expect(added.target).toBe("handler");
    expect(added.className).toContain("wf-rf-edge-error");
  });

  it("replaces an existing error edge rather than stacking a second", () => {
    const edges = [normal("a", "ok"), error("a", "handler1")];

    const next = setErrorRoute(edges, "a", "handler2");

    const errorEdges = next.filter((e) => e.source === "a" && e.sourceHandle === ERROR_HANDLE);
    expect(errorEdges).toHaveLength(1);
    expect(errorEdges[0].target).toBe("handler2");
  });

  it("clears the error edge when target is null, leaving normal edges", () => {
    const edges = [normal("a", "ok"), error("a", "handler")];

    const next = setErrorRoute(edges, "a", null);

    expect(next).toEqual([edges[0]]);
  });
});
