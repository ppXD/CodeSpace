import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { HarnessSelector } from "./HarnessSelector";

/**
 * The harness picker is dispatched via `x-selector: "harness"` on the `agent.code` node's config.
 * It lists the engine's registered harnesses and saves the chosen `kind` as a plain string.
 * Hook mocked: useHarnesses.
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

    expect(screen.getByRole("option", { name: "codex-cli" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "claude-code" })).toBeInTheDocument();

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "claude-code" } });
    expect(onChange).toHaveBeenCalledWith("claude-code");
  });

  it("reflects the currently-selected harness kind", () => {
    render(<HarnessSelector value="codex-cli" onChange={() => {}} />);
    expect((screen.getByRole("combobox") as HTMLSelectElement).value).toBe("codex-cli");
  });
});
