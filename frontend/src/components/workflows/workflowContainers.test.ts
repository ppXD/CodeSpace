import { describe, expect, it } from "vitest";

import { bodyStartTypeKey, CATCH_HANDLE, isBodyStartTypeKey, isContainerKind } from "./workflowContainers";

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
