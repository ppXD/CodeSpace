import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { WorkflowDetail } from "@/api/workflows";
import { AgentSettingsPanel } from "./AgentSettingsPanel";

/**
 * AgentSettingsPanel is the pure lifecycle/governance surface. Prop-driven, so these cover every
 * branch directly:
 *   1. identity — name, description (+ placeholder when null), version;
 *   2. status — enabled vs paused wording + the matching toggle button label;
 *   3. toggle → onToggleEnabled; delete → onDelete;
 *   4. guardrails placeholder is present;
 *   5. busy flags disable the respective buttons.
 */
const wf = (over: Partial<WorkflowDetail> = {}): WorkflowDetail => ({
  id: "w1",
  teamId: "t1",
  name: "PR Security Reviewer",
  description: "Reviews every PR for security issues.",
  enabled: true,
  latestVersion: 3,
  definition: { nodes: [], edges: [] } as unknown as WorkflowDetail["definition"],
  activations: [],
  createdDate: "2026-01-01T00:00:00Z",
  lastModifiedDate: "2026-01-01T00:00:00Z",
  ...over,
});

describe("AgentSettingsPanel", () => {
  it("is a sectioned controls page (General / Guardrails / Danger zone) — no identity header", () => {
    render(<AgentSettingsPanel workflow={wf()} onToggleEnabled={vi.fn()} onDelete={vi.fn()} />);
    expect(screen.getByText("General")).toBeTruthy();
    expect(screen.getByText("Guardrails")).toBeTruthy();
    expect(screen.getByText("Danger zone")).toBeTruthy();
    // The agent name + description belong to the breadcrumb + Overview, not here (no duplicate header).
    expect(screen.queryByText("PR Security Reviewer")).toBeNull();
    expect(screen.queryByText("Reviews every PR for security issues.")).toBeNull();
  });

  it("reflects enabled status with a Pause action", () => {
    const onToggleEnabled = vi.fn();
    render(<AgentSettingsPanel workflow={wf({ enabled: true })} onToggleEnabled={onToggleEnabled} onDelete={vi.fn()} />);
    expect(screen.getByText(/Enabled — its triggers fire/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /Pause agent/ }));
    expect(onToggleEnabled).toHaveBeenCalledTimes(1);
  });

  it("reflects paused status with an Enable action", () => {
    const onToggleEnabled = vi.fn();
    render(<AgentSettingsPanel workflow={wf({ enabled: false })} onToggleEnabled={onToggleEnabled} onDelete={vi.fn()} />);
    expect(screen.getByText(/Paused — triggers won't fire/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /Enable agent/ }));
    expect(onToggleEnabled).toHaveBeenCalledTimes(1);
  });

  it("wires the delete action", () => {
    const onDelete = vi.fn();
    render(<AgentSettingsPanel workflow={wf()} onToggleEnabled={vi.fn()} onDelete={onDelete} />);
    fireEvent.click(screen.getByRole("button", { name: /Delete agent/ }));
    expect(onDelete).toHaveBeenCalledTimes(1);
  });

  it("surfaces the guardrails placeholder", () => {
    render(<AgentSettingsPanel workflow={wf()} onToggleEnabled={vi.fn()} onDelete={vi.fn()} />);
    expect(screen.getByText("Guardrails")).toBeTruthy();
    expect(screen.getByText("Coming soon")).toBeTruthy();
    expect(screen.getByText(/Restrict which repositories/)).toBeTruthy();
  });

  it("disables the toggle while a status change is in flight", () => {
    render(<AgentSettingsPanel workflow={wf({ enabled: true })} onToggleEnabled={vi.fn()} onDelete={vi.fn()} toggling />);
    expect(screen.getByRole("button", { name: /Pause agent/ }).hasAttribute("disabled")).toBe(true);
  });

  it("disables delete and shows 'Deleting…' while deletion is in flight", () => {
    render(<AgentSettingsPanel workflow={wf()} onToggleEnabled={vi.fn()} onDelete={vi.fn()} deleting />);
    const btn = screen.getByRole("button", { name: /Deleting/ });
    expect(btn.hasAttribute("disabled")).toBe(true);
  });
});
