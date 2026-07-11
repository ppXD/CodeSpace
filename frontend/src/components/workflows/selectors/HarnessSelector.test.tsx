import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { HarnessSelector } from "./HarnessSelector";

/**
 * The harness picker (`x-selector: "harness"`) renders the shared SearchSelect combobox and saves the chosen
 * `kind`. Options appear once the search box is focused. Hook mocked: useHarnesses.
 */
vi.mock("@/hooks/use-agents", () => ({
  useHarnesses: () => ({
    isLoading: false,
    data: [
      { kind: "codex-cli", version: "1.0", models: ["gpt-5-codex"] },
      { kind: "claude-code", version: "0.9", models: ["claude-opus-4-8"] },
    ],
  }),
}));

describe("HarnessSelector", () => {
  it("lists registered harness kinds and emits the chosen kind", () => {
    const onChange = vi.fn();
    render(<HarnessSelector value="" onChange={onChange} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Pick a harness…" }));
    expect(screen.getByRole("option", { name: "codex-cli" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "claude-code" })).toBeInTheDocument();

    fireEvent.mouseDown(screen.getByRole("option", { name: "claude-code" }));
    expect(onChange).toHaveBeenCalledWith("claude-code");
  });

  it("reflects the currently-selected harness as a chip", () => {
    render(<HarnessSelector value="codex-cli" onChange={() => {}} />);
    expect(screen.getByText("codex-cli")).toBeInTheDocument();
  });
});
