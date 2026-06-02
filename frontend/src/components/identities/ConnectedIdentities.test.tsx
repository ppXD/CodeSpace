import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ConnectedIdentities } from "./ConnectedIdentities";

/**
 * The Connected-identities settings list: one row per provider instance, showing the caller's
 * linked username + Disconnect when linked, or Connect (opens the PAT modal) when not.
 */
const link = vi.fn();
const unlink = vi.fn();
let identities: Array<Record<string, unknown>> = [];

vi.mock("@/hooks/use-credentials", () => ({
  useProviderInstances: () => ({
    isLoading: false,
    data: [
      { id: "inst-gl", provider: "GitLab", displayName: "gitlab.com" },
      { id: "inst-gh", provider: "GitHub", displayName: "github.com" },
    ],
  }),
}));
vi.mock("@/hooks/use-identities", () => ({
  useMyProviderIdentities: () => ({ isLoading: false, data: identities }),
  useLinkIdentityByPat: () => ({ mutate: link, isPending: false }),
  useUnlinkIdentity: () => ({ mutate: unlink, isPending: false }),
}));

const linkedGitLab = { id: "id-1", providerInstanceId: "inst-gl", provider: "GitLab", providerUsername: "alice", providerUserId: "1", credentialStatus: "Active", createdDate: "" };

describe("ConnectedIdentities", () => {
  it("shows the username + Disconnect for a linked instance and Connect for an unlinked one", () => {
    identities = [linkedGitLab];
    render(<ConnectedIdentities />);

    expect(screen.getByText("GitLab · gitlab.com")).toBeInTheDocument();
    expect(screen.getByText("@alice")).toBeInTheDocument();
    expect(screen.getByText("GitHub · github.com")).toBeInTheDocument();
    expect(screen.getByText("Not connected")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Disconnect" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Connect" })).toBeInTheDocument();
  });

  it("disconnects a linked identity by its id", () => {
    unlink.mockClear();
    identities = [linkedGitLab];
    render(<ConnectedIdentities />);

    fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));

    expect(unlink).toHaveBeenCalledWith("id-1");
  });

  it("opens the link modal for an instance and submits the pasted token", () => {
    link.mockClear();
    identities = [];   // nothing linked → both rows show Connect
    render(<ConnectedIdentities />);

    // First Connect is the GitLab row (instances are listed in order).
    fireEvent.click(screen.getAllByRole("button", { name: "Connect" })[0]);

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText(/Connect to GitLab · gitlab.com/)).toBeInTheDocument();

    fireEvent.change(within(dialog).getByPlaceholderText(/glpat/i), { target: { value: "glpat-secret" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Connect" }));

    expect(link).toHaveBeenCalledTimes(1);
    expect(link.mock.calls[0][0]).toEqual({ providerInstanceId: "inst-gl", accessToken: "glpat-secret" });
  });
});
