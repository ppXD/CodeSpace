import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentDetailShell, type AgentTab } from "./AgentDetailShell";

/**
 * AgentDetailShell owns the agent-first tab behaviour. The mount policy is the load-bearing,
 * non-breaking guarantee — these pin every branch of it:
 *   1. default tab renders on mount; others don't;
 *   2. clicking a tab switches content; a non-keepMounted tab unmounts when left;
 *   3. a keepMounted tab (Source) is NOT mounted until first visited (no eager editor mount)…
 *   4. …and once visited, stays in the DOM hidden (display:none) when left — so the editor's
 *      unsaved state survives tab switches (the whole reason for keepMounted);
 *   5. render() is called only for mounted tabs (lazy);
 *   6. api.goTo lets a tab's content drive navigation (Overview's "Edit in Source");
 *   7. the leading slot renders before the tabs (back button).
 */
const TABS: AgentTab[] = [
  { key: "overview", label: "Overview" },
  { key: "activity", label: "Activity" },
  { key: "source", label: "Source", keepMounted: true },
];

describe("AgentDetailShell", () => {
  it("renders only the default tab's content on mount", () => {
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" render={(k) => <div>content:{k}</div>} />);
    expect(screen.getByText("content:overview")).toBeTruthy();
    expect(screen.queryByText("content:activity")).toBeNull();
    expect(screen.queryByText("content:source")).toBeNull();
  });

  it("switches content on tab click; a non-keepMounted tab unmounts when left", () => {
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" render={(k) => <div>content:{k}</div>} />);
    fireEvent.click(screen.getByRole("tab", { name: "Activity" }));
    expect(screen.getByText("content:activity")).toBeTruthy();
    expect(screen.queryByText("content:overview")).toBeNull(); // overview is not keepMounted
  });

  it("does NOT mount a keepMounted tab until it is first visited", () => {
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" render={(k) => <div>content:{k}</div>} />);
    expect(screen.queryByText("content:source")).toBeNull();
  });

  it("keeps a visited keepMounted tab mounted-but-hidden when left (preserves its state)", () => {
    const { container } = render(<AgentDetailShell tabs={TABS} defaultTab="overview" render={(k) => <div>content:{k}</div>} />);

    fireEvent.click(screen.getByRole("tab", { name: "Source" }));
    expect(screen.getByText("content:source")).toBeTruthy();

    fireEvent.click(screen.getByRole("tab", { name: "Overview" }));
    const sourcePane = container.querySelector('[data-tab="source"]') as HTMLElement | null;
    expect(sourcePane).not.toBeNull();
    expect(sourcePane!.style.display).toBe("none");      // hidden, not removed
    expect(screen.getByText("content:source")).toBeTruthy(); // still in the DOM
  });

  it("only calls render() for mounted tabs (lazy)", () => {
    const renderFn = vi.fn((k: string) => <div>content:{k}</div>);
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" render={renderFn} />);
    const keys = renderFn.mock.calls.map((c) => c[0]);
    expect(keys).toContain("overview");
    expect(keys).not.toContain("activity");
    expect(keys).not.toContain("source");
  });

  it("exposes goTo on the render api so content can switch tabs", () => {
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" render={(k, api) => (
      <button onClick={() => api.goTo("source")}>go-from:{k}</button>
    )} />);
    fireEvent.click(screen.getByText("go-from:overview"));
    expect(screen.getByText("go-from:source")).toBeTruthy();
  });

  it("renders the leading slot (e.g. back button) before the tabs", () => {
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" leading={<span>LEAD</span>} render={() => null} />);
    expect(screen.getByText("LEAD")).toBeTruthy();
  });
});
