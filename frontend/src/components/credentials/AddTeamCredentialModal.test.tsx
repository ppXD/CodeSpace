import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ProviderInstanceSummary } from "@/api/types";

import { AddTeamCredentialModal } from "./AddTeamCredentialModal";

const mutateAsync = vi.fn().mockResolvedValue({ id: "new-cred" });
vi.mock("@/hooks/use-credentials", () => ({
  useAddGroupAccessToken: () => ({ mutateAsync, isPending: false, error: null }),
}));

const gitlab: ProviderInstanceSummary = {
  id: "pi-gl", teamId: "t", provider: "GitLab", displayName: "gitlab.com",
  baseUrl: "https://gitlab.com", createdDate: "", oauthEnabled: true,
};

describe("AddTeamCredentialModal", () => {
  it("posts the pasted group token + name as a team credential, then signals added", async () => {
    mutateAsync.mockClear();
    const onAdded = vi.fn();
    render(<AddTeamCredentialModal instances={[gitlab]} onClose={() => {}} onAdded={onAdded} />);

    fireEvent.change(screen.getByPlaceholderText(/Acme team/), { target: { value: "Acme team" } });
    fireEvent.change(screen.getByPlaceholderText(/glpat/), { target: { value: "glpat-secret" } });
    fireEvent.click(screen.getByRole("button", { name: /add team credential/i }));

    await waitFor(() => expect(mutateAsync).toHaveBeenCalledWith({ providerInstanceId: "pi-gl", displayName: "Acme team", token: "glpat-secret" }));
    await waitFor(() => expect(onAdded).toHaveBeenCalled());
  });

  it("shows an empty state when there's no GitLab connection to attach the token to", () => {
    render(<AddTeamCredentialModal instances={[]} onClose={() => {}} onAdded={() => {}} />);
    expect(screen.getByText(/no gitlab connection/i)).toBeInTheDocument();
  });
});
