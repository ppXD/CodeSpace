import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ModelCredentialSelector } from "./ModelCredentialSelector";

/**
 * The credential picker lists the team's ACTIVE model credentials (name + provider) and saves the id;
 * the empty option means "fall back to the team/operator default". Hook mocked: useModelCredentials.
 */
vi.mock("@/hooks/use-model-credentials", () => ({
  useModelCredentials: () => ({
    isLoading: false,
    data: [
      { id: "c1", provider: "Anthropic", displayName: "Team Anthropic", status: "Active" },
      { id: "c2", provider: "OpenAI", displayName: "Old key", status: "Revoked" },
    ],
  }),
}));

describe("ModelCredentialSelector", () => {
  it("lists only active credentials and emits the chosen id", () => {
    const onChange = vi.fn();
    render(<ModelCredentialSelector value="" onChange={onChange} />);

    expect(screen.getByRole("option", { name: "Team Anthropic (Anthropic)" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Old key (OpenAI)" })).not.toBeInTheDocument();   // revoked → hidden
    expect(screen.getByRole("option", { name: "Team / operator default" })).toBeInTheDocument();

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "c1" } });
    expect(onChange).toHaveBeenCalledWith("c1");
  });
});
