import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/request";
import type { CredentialSummary } from "@/api/types";

import { ReplaceTokenModal } from "./ReplaceTokenModal";

/**
 * Replacing a credential's token in place: token gated (Replace disabled until typed), submits the credential's id and the
 * trimmed token, calls onReplaced+onClose on success, and keeps the dialog open with the API's own message on a refusal —
 * a different account is a new connection, and the message says to connect it separately.
 */
const mutate = vi.fn();
vi.mock("@/hooks/use-credentials", () => ({
  useReplaceCredentialToken: () => ({ mutate, isPending: false }),
}));

const credential: CredentialSummary = {
  id: "cred-1", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal",
  authType: "Pat", displayName: "GitLab · nacho", status: "Active", createdDate: "2026-01-01T00:00:00Z",
};

function renderModal() {
  const onClose = vi.fn();
  const onReplaced = vi.fn();
  render(<ReplaceTokenModal credential={credential} providerLabel="GitLab · gitlab.com" onClose={onClose} onReplaced={onReplaced} />);
  return { onClose, onReplaced };
}

describe("ReplaceTokenModal", () => {
  it("says the credential keeps what is bound to it, and keeps Replace disabled until a token is entered", () => {
    mutate.mockClear();
    renderModal();

    expect(screen.getByText(/GitLab · nacho keeps its repositories and webhooks; nothing is moved/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Replace token" })).toBeDisabled();
  });

  it("submits the credential's id with the trimmed token", () => {
    mutate.mockClear();
    renderModal();

    fireEvent.change(screen.getByPlaceholderText(/glpat/i), { target: { value: "  glpat-fresh  " } });
    fireEvent.click(screen.getByRole("button", { name: "Replace token" }));

    expect(mutate.mock.calls[0][0]).toEqual({ credentialId: "cred-1", token: "glpat-fresh" });
  });

  it("calls onReplaced and onClose on success", () => {
    mutate.mockClear();
    mutate.mockImplementation((_vars, opts) => opts.onSuccess());
    const { onClose, onReplaced } = renderModal();

    fireEvent.change(screen.getByPlaceholderText(/glpat/i), { target: { value: "glpat-fresh" } });
    fireEvent.click(screen.getByRole("button", { name: "Replace token" }));

    expect(onReplaced).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("shows the refusal and stays open when the token belongs to another account", () => {
    mutate.mockClear();
    const message = "The credential 'GitLab · nacho' belongs to the nacho account, and the authorization you gave is for maya. Re-authorizing means the same account signing in again; connect maya as its own credential instead, or sign in as nacho.";
    mutate.mockImplementation((_vars, opts) => opts.onError(new ApiError(409, "credential_account_mismatch", message)));
    const { onClose, onReplaced } = renderModal();

    fireEvent.change(screen.getByPlaceholderText(/glpat/i), { target: { value: "glpat-mayas" } });
    fireEvent.click(screen.getByRole("button", { name: "Replace token" }));

    expect(screen.getByText(message)).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
    expect(onReplaced).not.toHaveBeenCalled();
  });
});
