import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { NodeKind, NodeManifestDto } from "@/api/workflows";

import { NodeAddMenu } from "./NodeAddMenu";

const manifest = (typeKey: string, kind: NodeKind, displayName: string): NodeManifestDto => ({
  typeKey, displayName, category: "Logic", kind, description: null, iconKey: null,
  configSchema: {}, inputSchema: {}, outputSchema: {},
});

const manifests: NodeManifestDto[] = [
  manifest("trigger.manual", "Trigger", "Manual start"),
  manifest("flow.loop_start", "Regular", "Loop start"),
  manifest("http.request", "Regular", "HTTP request"),
  manifest("flow.loop", "Loop", "Loop"),
  manifest("flow.terminal", "Terminal", "Done"),
];

describe("NodeAddMenu", () => {
  it("offers steps + endpoints, hides triggers and the loop_start marker", () => {
    render(<NodeAddMenu at={{ x: 10, y: 10 }} manifests={manifests} onPick={() => {}} onClose={() => {}} />);

    expect(screen.getByText("HTTP request")).toBeTruthy();
    expect(screen.getByText("Loop")).toBeTruthy();
    expect(screen.getByText("Done")).toBeTruthy();
    expect(screen.queryByText("Manual start")).toBeNull(); // never add a second trigger
    expect(screen.queryByText("Loop start")).toBeNull();   // internal marker, auto-created with a Loop
  });

  it("calls onPick with the chosen manifest", () => {
    const onPick = vi.fn();
    render(<NodeAddMenu at={{ x: 0, y: 0 }} manifests={manifests} onPick={onPick} onClose={() => {}} />);

    fireEvent.click(screen.getByText("HTTP request"));
    expect(onPick).toHaveBeenCalledWith(expect.objectContaining({ typeKey: "http.request" }));
  });

  it("closes when the backdrop is clicked", () => {
    const onClose = vi.fn();
    render(<NodeAddMenu at={{ x: 0, y: 0 }} manifests={manifests} onPick={() => {}} onClose={onClose} />);

    fireEvent.click(document.querySelector(".wf-addmenu-mask")!);
    expect(onClose).toHaveBeenCalled();
  });
});
