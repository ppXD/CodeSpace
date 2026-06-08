import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentDetailTabs, type AgentTab } from "./AgentDetailTabs";

/**
 * AgentDetailTabs is the data-driven tab strip for the Agent detail shell. These pin the generic
 * contract so the shell stays trivial to extend:
 *   1. renders one tab per entry, in order, with its label;
 *   2. marks exactly the active tab (aria-selected + data-active) — the rest are inactive;
 *   3. clicking a tab reports its `key` (not its index/label) to onChange;
 *   4. the icon slot is optional (absent icon → no glyph, no crash);
 *   5. an empty tab set renders an empty, valid tablist (no throw).
 */
const TABS: AgentTab[] = [
  { key: "overview", label: "Overview" },
  { key: "activity", label: "Activity" },
  { key: "source", label: "Source", icon: <span>{"</>"}</span> },
];

describe("AgentDetailTabs", () => {
  it("renders one tab per entry with its label, in order", () => {
    render(<AgentDetailTabs tabs={TABS} active="overview" onChange={vi.fn()} />);
    const tabs = screen.getAllByRole("tab");
    expect(tabs.map((t) => t.textContent)).toEqual(["Overview", "Activity", "</>Source"]);
  });

  it("marks exactly the active tab as selected", () => {
    render(<AgentDetailTabs tabs={TABS} active="activity" onChange={vi.fn()} />);
    expect(screen.getByRole("tab", { name: "Activity" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tab", { name: "Overview" }).getAttribute("aria-selected")).toBe("false");
    expect(screen.getByRole("tab", { name: "Source" }).getAttribute("aria-selected")).toBe("false");
  });

  it("reports the tab key (not label/index) on click", () => {
    const onChange = vi.fn();
    render(<AgentDetailTabs tabs={TABS} active="overview" onChange={onChange} />);
    fireEvent.click(screen.getByRole("tab", { name: "Activity" }));
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith("activity");
  });

  it("clicking the already-active tab still reports its key (parent decides to no-op)", () => {
    const onChange = vi.fn();
    render(<AgentDetailTabs tabs={TABS} active="overview" onChange={onChange} />);
    fireEvent.click(screen.getByRole("tab", { name: "Overview" }));
    expect(onChange).toHaveBeenCalledWith("overview");
  });

  it("renders without an icon when none is provided, and with one when present", () => {
    render(<AgentDetailTabs tabs={TABS} active="overview" onChange={vi.fn()} />);
    // Source declares an icon → glyph present; Overview does not → label only.
    expect(screen.getByRole("tab", { name: "Source" }).querySelector(".agent-tab-ic")).not.toBeNull();
    expect(screen.getByRole("tab", { name: "Overview" }).querySelector(".agent-tab-ic")).toBeNull();
  });

  it("renders an empty, valid tablist for an empty tab set", () => {
    render(<AgentDetailTabs tabs={[]} active="" onChange={vi.fn()} />);
    expect(screen.getByRole("tablist")).toBeTruthy();
    expect(screen.queryAllByRole("tab")).toHaveLength(0);
  });
});
