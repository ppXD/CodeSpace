import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { NodePreset } from "@/api/workflows";

import { NodePresetPicker } from "./NodePresetPicker";

const presets: NodePreset[] = [
  { id: "quorum_review", label: "Quorum review", description: "N approvals; any block stops it", config: { resolve: { mode: "quorum", count: 2 } }, inputs: { actions: [{ key: "approve" }] } },
  { id: "form", label: "Form", description: null, config: {}, inputs: { form: { fields: {} } } },
];

describe("NodePresetPicker", () => {
  it("renders one button per preset with its label + description", () => {
    render(<NodePresetPicker presets={presets} onApply={vi.fn()} />);
    expect(screen.getByRole("button", { name: /Quorum review/ })).toBeInTheDocument();
    expect(screen.getByText("N approvals; any block stops it")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Form/ })).toBeInTheDocument();
  });

  it("applies the picked preset (the whole config+inputs pair) on click", () => {
    const onApply = vi.fn();
    render(<NodePresetPicker presets={presets} onApply={onApply} />);
    fireEvent.click(screen.getByRole("button", { name: /Quorum review/ }));
    expect(onApply).toHaveBeenCalledWith(presets[0]);
  });

  it("renders nothing when the node declares no presets", () => {
    const { container } = render(<NodePresetPicker presets={[]} onApply={vi.fn()} />);
    expect(container.firstChild).toBeNull();
  });
});
