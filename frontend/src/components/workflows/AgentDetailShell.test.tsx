import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentDetailShell, type AgentTab } from "./AgentDetailShell";

/**
 * AgentDetailShell — the agent-first tab behaviour on the shared `.ct` container-page system.
 * The mount policy is the load-bearing, non-breaking guarantee:
 *   1. default tab renders (in a padded `.ct-body`); others don't;
 *   2. clicking switches content; a non-keepMounted tab unmounts when left;
 *   3. a keepMounted tab (Source) is NOT mounted until first visited (no eager editor mount)…
 *   4. …and once visited stays mounted-but-hidden when left (the editor's unsaved state survives);
 *   5. render() runs only for mounted tabs (lazy);
 *   6. a `fill` tab renders edge-to-edge (`.agent-source-pane`), normal tabs in `.ct-body`;
 *   7. api.goTo lets content switch tabs; the breadcrumb (crumbs) renders in the head.
 */
const TABS: AgentTab[] = [
  { key: "overview", label: "Overview" },
  { key: "activity", label: "Activity" },
  { key: "source", label: "Source", keepMounted: true, fill: true },
];

describe("AgentDetailShell", () => {
  it("renders only the default tab's content on mount, inside a .ct-body", () => {
    const { container } = render(<AgentDetailShell tabs={TABS} defaultTab="overview" render={(k) => <div>content:{k}</div>} />);
    expect(screen.getByText("content:overview")).toBeTruthy();
    expect(screen.queryByText("content:activity")).toBeNull();
    expect(container.querySelector('.ct-body[data-tab="overview"]')).not.toBeNull();
  });

  it("switches content on tab click; a non-keepMounted tab unmounts when left", () => {
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" render={(k) => <div>content:{k}</div>} />);
    fireEvent.click(screen.getByRole("tab", { name: "Activity" }));
    expect(screen.getByText("content:activity")).toBeTruthy();
    expect(screen.queryByText("content:overview")).toBeNull();
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
    expect(sourcePane!.style.display).toBe("none");
    expect(screen.getByText("content:source")).toBeTruthy();
  });

  it("renders a fill tab edge-to-edge (.agent-source-pane), normal tabs in .ct-body", () => {
    const { container } = render(<AgentDetailShell tabs={TABS} defaultTab="source" render={(k) => <div>content:{k}</div>} />);
    expect(container.querySelector('.agent-source-pane[data-tab="source"]')).not.toBeNull();
    expect(container.querySelector('[data-tab="overview"]')).toBeNull(); // not active, not keepMounted → unmounted
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

  it("renders the breadcrumb (crumbs) in the head", () => {
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" crumbs={<span>Agents / Reviewer</span>} render={() => null} />);
    expect(screen.getByText("Agents / Reviewer")).toBeTruthy();
  });

  // Controlled mode — the route drives `active` from the URL (?tab=) so links are deep-linkable.
  it("controlled: renders the `active` tab rather than the default", () => {
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" active="activity" onActiveChange={vi.fn()} render={(k) => <div>content:{k}</div>} />);
    expect(screen.getByText("content:activity")).toBeTruthy();
    expect(screen.queryByText("content:overview")).toBeNull();
  });

  it("controlled: a tab click calls onActiveChange and lets the parent drive the switch", () => {
    const onActiveChange = vi.fn();
    render(<AgentDetailShell tabs={TABS} defaultTab="overview" active="overview" onActiveChange={onActiveChange} render={(k) => <div>content:{k}</div>} />);
    fireEvent.click(screen.getByRole("tab", { name: "Activity" }));
    expect(onActiveChange).toHaveBeenCalledWith("activity");
    // The shell does not self-switch when controlled — content follows the prop, still overview.
    expect(screen.getByText("content:overview")).toBeTruthy();
  });

  it("controlled: a keepMounted tab stays mounted-but-hidden after the controlled tab changes", () => {
    const { container, rerender } = render(<AgentDetailShell tabs={TABS} defaultTab="overview" active="source" onActiveChange={vi.fn()} render={(k) => <div>content:{k}</div>} />);
    expect(screen.getByText("content:source")).toBeTruthy();

    rerender(<AgentDetailShell tabs={TABS} defaultTab="overview" active="overview" onActiveChange={vi.fn()} render={(k) => <div>content:{k}</div>} />);

    const sourcePane = container.querySelector('[data-tab="source"]') as HTMLElement | null;
    expect(sourcePane).not.toBeNull();
    expect(sourcePane!.style.display).toBe("none");
    expect(screen.getByText("content:source")).toBeTruthy();
  });
});
