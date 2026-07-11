import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ModelCredentialSelector } from "./ModelCredentialSelector";

/**
 * The credential picker (`x-selector: "modelCredential"`) renders the shared SearchSelect combobox, listing
 * ACTIVE credentials (name · provider) and saving the id; empty = the team/operator default. With `providers`
 * it shows only drivable credentials, and a now-incompatible saved credential stays visible (flagged). Hook
 * mocked: useModelCredentials.
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

    fireEvent.focus(screen.getByRole("textbox", { name: "Team / operator default" }));
    expect(screen.getByRole("option", { name: /Team Anthropic/ })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Old key/ })).not.toBeInTheDocument();   // revoked → hidden

    fireEvent.mouseDown(screen.getByRole("option", { name: /Team Anthropic/ }));
    expect(onChange).toHaveBeenCalledWith("c1");
  });

  it("filters to the harness's drivable providers", () => {
    render(<ModelCredentialSelector value="" onChange={() => {}} providers={["Anthropic", "Custom"]} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Team / operator default" }));
    expect(screen.getByRole("option", { name: /Team Anthropic/ })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Team OpenAI/ })).not.toBeInTheDocument();   // OpenAI not drivable here
  });

  it("keeps a now-incompatible saved credential visible but flagged, never silently blanked", () => {
    render(<ModelCredentialSelector value="c2" onChange={() => {}} providers={["Anthropic"]} />);

    // c2 (OpenAI) shows as a chip even though the harness drives only Anthropic — flagged, never blanked.
    expect(screen.getByText(/Team OpenAI — incompatible with this harness/)).toBeInTheDocument();
  });
});
