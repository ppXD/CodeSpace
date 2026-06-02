import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/request";

import { IdentityLinkModal } from "./IdentityLinkModal";

/**
 * The generic connect-identity dialog: token gated (Connect disabled until typed), submits
 * {providerInstanceId, accessToken}, calls onLinked+onClose on success, surfaces the API error
 * message on failure (no close).
 */
const mutate = vi.fn();
vi.mock("@/hooks/use-identities", () => ({
  useLinkIdentityByPat: () => ({ mutate, isPending: false }),
}));

function renderModal(overrides: Partial<{ onClose: () => void; onLinked: () => void }> = {}) {
  const onClose = overrides.onClose ?? vi.fn();
  const onLinked = overrides.onLinked ?? vi.fn();
  render(<IdentityLinkModal providerInstanceId="inst-1" providerLabel="GitLab · gitlab.com" onClose={onClose} onLinked={onLinked} />);
  return { onClose, onLinked };
}

describe("IdentityLinkModal", () => {
  it("keeps Connect disabled until a token is entered, then submits it", () => {
    mutate.mockClear();
    renderModal();

    const connect = screen.getByRole("button", { name: "Connect" });
    expect(connect).toBeDisabled();

    fireEvent.change(screen.getByPlaceholderText(/glpat/i), { target: { value: "  glpat-xyz  " } });
    expect(connect).toBeEnabled();

    fireEvent.click(connect);
    expect(mutate.mock.calls[0][0]).toEqual({ providerInstanceId: "inst-1", accessToken: "glpat-xyz" });
  });

  it("calls onLinked + onClose on success", () => {
    mutate.mockClear();
    mutate.mockImplementation((_vars, opts) => opts.onSuccess());
    const { onClose, onLinked } = renderModal();

    fireEvent.change(screen.getByPlaceholderText(/glpat/i), { target: { value: "glpat-xyz" } });
    fireEvent.click(screen.getByRole("button", { name: "Connect" }));

    expect(onLinked).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("surfaces the API error message and stays open on failure", () => {
    mutate.mockClear();
    mutate.mockImplementation((_vars, opts) => opts.onError(new ApiError(422, "provider_unauthorized", "Token rejected by GitLab")));
    const { onClose } = renderModal();

    fireEvent.change(screen.getByPlaceholderText(/glpat/i), { target: { value: "bad" } });
    fireEvent.click(screen.getByRole("button", { name: "Connect" }));

    expect(screen.getByText("Token rejected by GitLab")).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });
});
