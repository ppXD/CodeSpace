import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  authorAgent: vi.fn(),
  authorSkill: vi.fn(),
  useAuthorStoreAgent: vi.fn(),
  useAuthorStoreSkill: vi.fn(),
}));

vi.mock("@/hooks/use-agents", () => ({ useAuthorStoreAgent: h.useAuthorStoreAgent }));
vi.mock("@/hooks/use-skills", () => ({ useAuthorStoreSkill: h.useAuthorStoreSkill }));

import { AuthorIntoLibraryModal } from "./AuthorIntoLibraryModal";

function setup() {
  h.authorAgent.mockResolvedValue({ id: "a1" });
  h.authorSkill.mockResolvedValue({ id: "s1" });
  h.useAuthorStoreAgent.mockReturnValue({ mutateAsync: h.authorAgent, isPending: false, error: null });
  h.useAuthorStoreSkill.mockReturnValue({ mutateAsync: h.authorSkill, isPending: false, error: null });
  const onClose = vi.fn();
  render(<AuthorIntoLibraryModal onClose={onClose} />);
  return { onClose };
}

describe("AuthorIntoLibraryModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("offers Agent and Skill on-ramps", () => {
    setup();
    expect(screen.getByRole("button", { name: /Agent/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Skill/ })).toBeInTheDocument();
  });

  it("authors an agent into the Library with the entered fields and closes", async () => {
    const { onClose } = setup();
    fireEvent.click(screen.getByRole("button", { name: /Agent/ }));
    fireEvent.change(screen.getByLabelText(/Name/), { target: { value: "Security Reviewer" } });
    fireEvent.change(screen.getByLabelText(/System prompt/), { target: { value: "Audit for vulns." } });
    fireEvent.click(screen.getByRole("button", { name: /Add to Library/ }));

    await waitFor(() => expect(h.authorAgent).toHaveBeenCalledWith({ name: "Security Reviewer", description: null, systemPrompt: "Audit for vulns." }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(h.authorSkill).not.toHaveBeenCalled();
  });

  it("authors a skill into the Library with body + category", async () => {
    setup();
    fireEvent.click(screen.getByRole("button", { name: /Skill/ }));
    fireEvent.change(screen.getByLabelText(/Name/), { target: { value: "Threat Modeling" } });
    fireEvent.change(screen.getByLabelText(/SKILL\.md/), { target: { value: "Use STRIDE." } });
    fireEvent.change(screen.getByLabelText(/Category/), { target: { value: "security" } });
    fireEvent.click(screen.getByRole("button", { name: /Add to Library/ }));

    await waitFor(() => expect(h.authorSkill).toHaveBeenCalledWith({ name: "Threat Modeling", description: null, body: "Use STRIDE.", category: "security" }));
  });

  it("disables Add until a name is entered", () => {
    setup();
    fireEvent.click(screen.getByRole("button", { name: /Agent/ }));
    expect(screen.getByRole("button", { name: /Add to Library/ })).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/Name/), { target: { value: "X" } });
    expect(screen.getByRole("button", { name: /Add to Library/ })).not.toBeDisabled();
  });
});
