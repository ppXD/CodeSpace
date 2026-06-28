import { describe, expect, it } from "vitest";

import { toolsLabel } from "./agentRuntime";

describe("toolsLabel", () => {
  it("maps the tri-state", () => {
    expect(toolsLabel(null)).toBe("Default tools");
    expect(toolsLabel([])).toBe("No tools");
    expect(toolsLabel(["read"])).toBe("1 tool");
    expect(toolsLabel(["read", "edit", "run"])).toBe("3 tools");
  });
});
