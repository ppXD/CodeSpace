import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ModelCredentialSelector } from "./ModelCredentialSelector";

/**
 * The credential picker lists the team's ACTIVE model credentials (name + provider), saving the id; the
 * empty option means "fall back to the team/operator default". When given `providers` (the harness's
 * drivable set), it shows only matching credentials. Hook mocked: useModelCredentials.
 */
vi.mock("@/hooks/use-model-credentials", () => ({
  useModelCredentials: () => ({
    isLoading: false,
    data: [
      { id: "c1", provider: "Anthropic", displayName: "Team Anthropic", status: "Active" },
      { id: "c2", provider: "OpenAI", displayName: "Team OpenAI", status: "Active" },
      { id: "c3", provider: "OpenAI", displayName: "Old key", status: "Revoked" },
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

  it("filters to the harness's drivable providers", () => {
    render(<ModelCredentialSelector value="" onChange={() => {}} providers={["Anthropic", "Custom"]} />);

    expect(screen.getByRole("option", { name: "Team Anthropic (Anthropic)" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Team OpenAI (OpenAI)" })).not.toBeInTheDocument();   // OpenAI not drivable here
  });

  it("keeps a now-incompatible saved credential visible but flagged, never silently blanked", () => {
    render(<ModelCredentialSelector value="c2" onChange={() => {}} providers={["Anthropic"]} />);

    // c2 is OpenAI but the harness only drives Anthropic — shown flagged so the field still reflects the saved value.
    const flagged = screen.getByRole("option", { name: /Team OpenAI \(OpenAI\) — incompatible/ });
    expect(flagged).toBeInTheDocument();
    expect((screen.getByRole("combobox") as HTMLSelectElement).value).toBe("c2");
  });
});
