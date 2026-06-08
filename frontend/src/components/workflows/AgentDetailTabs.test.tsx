import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentDetailTabs, type AgentTab } from "./AgentDetailTabs";

/**
 * AgentDetailTabs emits the app's shared `.ct-tabs` / `.ct-tab` underline tabs (same as the Project
 * page) for visual consistency. These pin the contract:
 *   1. one `.ct-tab` per entry, in order, with its label;
 *   2. exactly the active tab is marked (data-active + aria-selected);
 *   3. click reports the tab `key`; Enter/Space do too (keyboard a11y);
 *   4. the `.ct-tab-c` count badge shows only when `count` is provided;
 *   5. an empty set renders an empty, valid tablist.
 */
const TABS: AgentTab[] = [
  { key: "overview", label: "Overview" },
  { key: "activity", label: "Activity", count: 5 },
  { key: "source", label: "Source" },
];

describe("AgentDetailTabs", () => {
  it("renders one tab per entry with its label, in order", () => {
    render(<AgentDetailTabs tabs={TABS} active="overview" onChange={vi.fn()} />);
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual(["Overview", "Activity5", "Source"]);
  });

  it("marks exactly the active tab", () => {
    render(<AgentDetailTabs tabs={TABS} active="activity" onChange={vi.fn()} />);
    expect(screen.getByRole("tab", { name: /Activity/ }).getAttribute("data-active")).toBe("true");
    expect(screen.getByRole("tab", { name: "Overview" }).getAttribute("data-active")).toBe("false");
    expect(screen.getByRole("tab", { name: /Activity/ }).getAttribute("aria-selected")).toBe("true");
  });

  it("reports the tab key on click", () => {
    const onChange = vi.fn();
    render(<AgentDetailTabs tabs={TABS} active="overview" onChange={onChange} />);
    fireEvent.click(screen.getByRole("tab", { name: "Source" }));
    expect(onChange).toHaveBeenCalledWith("source");
  });

  it("reports the tab key on Enter (keyboard a11y)", () => {
    const onChange = vi.fn();
    render(<AgentDetailTabs tabs={TABS} active="overview" onChange={onChange} />);
    fireEvent.keyDown(screen.getByRole("tab", { name: "Source" }), { key: "Enter" });
    expect(onChange).toHaveBeenCalledWith("source");
  });

  it("shows the count badge only when count is provided", () => {
    render(<AgentDetailTabs tabs={TABS} active="overview" onChange={vi.fn()} />);
    expect(screen.getByRole("tab", { name: /Activity/ }).querySelector(".ct-tab-c")?.textContent).toBe("5");
    expect(screen.getByRole("tab", { name: "Overview" }).querySelector(".ct-tab-c")).toBeNull();
  });

  it("renders an empty, valid tablist for an empty tab set", () => {
    render(<AgentDetailTabs tabs={[]} active="" onChange={vi.fn()} />);
    expect(screen.getByRole("tablist")).toBeTruthy();
    expect(screen.queryAllByRole("tab")).toHaveLength(0);
  });
});
