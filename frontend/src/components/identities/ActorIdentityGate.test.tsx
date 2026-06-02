import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/request";

import { ActorIdentityProvider, useActorIdentityGate } from "./ActorIdentityGate";

// The gate renders IdentityLinkModal, which calls useLinkIdentityByPat. Stub it so a click on
// "Connect" drives the success path without a network.
const mutate = vi.fn();
vi.mock("@/hooks/use-identities", () => ({
  useLinkIdentityByPat: () => ({ mutate, isPending: false }),
}));

function Consumer({ error, retry }: { error: unknown; retry: () => void }) {
  const gate = useActorIdentityGate();
  return <button onClick={() => gate.prompt(error, retry)}>prompt</button>;
}

const actorErr = new ApiError(428, "actor_identity_required", "link required", { provider: "GitHub", providerInstanceId: "inst-1" });

describe("ActorIdentityGate", () => {
  it("opens the link modal for the named provider on a 428, then retries after a successful link", () => {
    mutate.mockReset();
    const retry = vi.fn();
    render(<ActorIdentityProvider><Consumer error={actorErr} retry={retry} /></ActorIdentityProvider>);

    fireEvent.click(screen.getByText("prompt"));

    // Modal targets the named provider.
    expect(screen.getByText("Connect to GitHub")).toBeInTheDocument();

    // A successful link resolves the mutation → IdentityLinkModal fires onLinked → gate retries.
    mutate.mockImplementation((_vars, opts) => opts.onSuccess());
    fireEvent.change(screen.getByPlaceholderText(/glpat/i), { target: { value: "glpat-xyz" } });
    fireEvent.click(screen.getByRole("button", { name: "Connect" }));

    expect(retry).toHaveBeenCalledTimes(1);
  });

  it("ignores a non-actor-identity error (no modal)", () => {
    render(<ActorIdentityProvider><Consumer error={new Error("boom")} retry={vi.fn()} /></ActorIdentityProvider>);

    fireEvent.click(screen.getByText("prompt"));

    expect(screen.queryByPlaceholderText(/glpat/i)).not.toBeInTheDocument();
  });
});
