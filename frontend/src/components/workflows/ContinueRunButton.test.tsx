import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// The button drives the continue mutation through this hook; stub it so the test controls the mutation state.
const continueMock: { mutate: ReturnType<typeof vi.fn>; isPending: boolean } = { mutate: vi.fn(), isPending: false };
vi.mock("@/hooks/use-workflows", () => ({
  useContinueRun: () => continueMock,
}));

import { ContinueRunButton } from "./ContinueRunButton";

beforeEach(() => {
  continueMock.mutate = vi.fn();
  continueMock.isPending = false;
});

describe("ContinueRunButton", () => {
  it("renders nothing for a terminal run", () => {
    const { container } = render(<ContinueRunButton runId="r1" status="Failure" hasPendingWait={false} />);
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing for a Suspended run that still has a pending wait (it resumes via its wait, not continue)", () => {
    const { container } = render(<ContinueRunButton runId="r1" status="Suspended" hasPendingWait={true} />);
    expect(container.firstChild).toBeNull();
  });

  it("shows Continue ONLY for a stranded Suspended run (no pending wait)", () => {
    render(<ContinueRunButton runId="r1" status="Suspended" hasPendingWait={false} />);
    expect(screen.getByRole("button", { name: /^continue$/i })).toBeInTheDocument();
  });

  it("drives the continue mutation on click", () => {
    render(<ContinueRunButton runId="r1" status="Suspended" hasPendingWait={false} />);
    fireEvent.click(screen.getByRole("button", { name: /^continue$/i }));
    expect(continueMock.mutate).toHaveBeenCalledTimes(1);
  });

  it("shows a disabled pending state while continuing", () => {
    continueMock.isPending = true;
    render(<ContinueRunButton runId="r1" status="Suspended" hasPendingWait={false} />);
    expect(screen.getByRole("button", { name: /continuing/i })).toBeDisabled();
  });
});
