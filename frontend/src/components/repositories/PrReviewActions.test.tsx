import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/request";

import { PrReviewActions } from "./PrReviewActions";

// The component owns the submit mutation + the identity gate; stub both so we can assert exactly
// what a click sends and where a 428 is routed, without a network or a provider tree.
const mutate = vi.fn();
vi.mock("@/hooks/use-repositories", () => ({
  useSubmitPullRequestReview: () => ({ mutate, isPending: false }),
}));

const prompt = vi.fn(() => true);
vi.mock("@/components/identities/ActorIdentityGate", () => ({
  useActorIdentityGate: () => ({ prompt }),
}));

describe("PrReviewActions", () => {
  beforeEach(() => { mutate.mockReset(); prompt.mockClear(); });

  it("submits Approve with no body by default", () => {
    render(<PrReviewActions repoId="r1" number={3} />);

    fireEvent.click(screen.getByRole("button", { name: "Submit review" }));

    expect(mutate.mock.calls[0][0]).toEqual({ verdict: "Approve", body: null });
  });

  it("requires a comment for Comment / RequestChanges before enabling submit", () => {
    render(<PrReviewActions repoId="r1" number={3} />);

    fireEvent.click(screen.getByRole("radio", { name: "Comment" }));
    const submit = screen.getByRole("button", { name: "Submit review" });
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByRole("textbox"), { target: { value: "  one nit  " } });
    expect(submit).toBeEnabled();

    fireEvent.click(submit);
    expect(mutate.mock.calls[0][0]).toEqual({ verdict: "Comment", body: "one nit" });
  });

  it("routes a 428 actor_identity_required to the gate with a retry, not an inline error", () => {
    const err = new ApiError(428, "actor_identity_required", "link required", { provider: "GitHub", providerInstanceId: "i1" });
    mutate.mockImplementation((_vars, opts) => opts.onError(err));

    render(<PrReviewActions repoId="r1" number={3} />);
    fireEvent.click(screen.getByRole("button", { name: "Submit review" }));

    expect(prompt).toHaveBeenCalledWith(err, expect.any(Function));
    expect(screen.queryByText("link required")).not.toBeInTheDocument();
  });

  it("surfaces a non-identity error inline when the gate declines it", () => {
    prompt.mockReturnValueOnce(false);
    const err = new ApiError(403, "forbidden", "no access");
    mutate.mockImplementation((_vars, opts) => opts.onError(err));

    render(<PrReviewActions repoId="r1" number={3} />);
    fireEvent.click(screen.getByRole("button", { name: "Submit review" }));

    expect(screen.getByText("no access")).toBeInTheDocument();
  });
});
