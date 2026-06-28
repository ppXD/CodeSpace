import { describe, expect, it } from "vitest";

import { autonomySummary, toolsLabel } from "./agentRuntime";

describe("autonomySummary", () => {
  it("describes each tier (case-insensitive)", () => {
    expect(autonomySummary("Confined")).toMatch(/analysis only/i);
    expect(autonomySummary("trusted")).toMatch(/network/i);
    expect(autonomySummary("UNLEASHED")).toMatch(/no approval gates/i);
    expect(autonomySummary("Standard")).toMatch(/safe default/i);
  });

  it("falls back to Standard for unknown / blank / null", () => {
    const standard = autonomySummary("Standard");
    expect(autonomySummary("")).toBe(standard);
    expect(autonomySummary(null)).toBe(standard);
    expect(autonomySummary("bananas")).toBe(standard);
  });
});

describe("toolsLabel", () => {
  it("maps the tri-state", () => {
    expect(toolsLabel(null)).toBe("Default tools");
    expect(toolsLabel([])).toBe("No tools");
    expect(toolsLabel(["read"])).toBe("1 tool");
    expect(toolsLabel(["read", "edit", "run"])).toBe("3 tools");
  });
});
