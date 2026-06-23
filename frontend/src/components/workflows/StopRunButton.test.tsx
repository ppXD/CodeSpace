import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// The button drives the cancel mutation + terminal gate through this hook module; stub it so the test controls both
// the run's liveness (isRunActive) and the mutation state (pending/error) without a QueryClient or a real fetch.
const cancelMock: { mutate: ReturnType<typeof vi.fn>; isPending: boolean; isError: boolean } = {
  mutate: vi.fn(), isPending: false, isError: false,
};
vi.mock("@/hooks/use-workflows", () => ({
  useCancelRun: () => cancelMock,
  isRunActive: (s: string) => !["Success", "Failure", "Cancelled"].includes(s),
}));

import { StopRunButton } from "./StopRunButton";

beforeEach(() => {
  cancelMock.mutate = vi.fn();
  cancelMock.isPending = false;
  cancelMock.isError = false;
});

describe("StopRunButton", () => {
  it("renders nothing for a terminal run", () => {
    const { container } = render(<StopRunButton runId="r1" status="Success" />);
    expect(container.firstChild).toBeNull();
  });

  it("shows a single Stop button for a live run", () => {
    render(<StopRunButton runId="r1" status="Running" />);
    expect(screen.getByRole("button", { name: /^stop$/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /confirm stop/i })).toBeNull();
  });

  it("arms a two-step confirm before cancelling — no mutate on the first click", () => {
    render(<StopRunButton runId="r1" status="Running" />);

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    expect(screen.getByRole("button", { name: /confirm stop/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /keep running/i })).toBeInTheDocument();
    expect(cancelMock.mutate).not.toHaveBeenCalled();
  });

  it("cancels the run only after the confirm click", () => {
    render(<StopRunButton runId="r1" status="Running" />);

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    fireEvent.click(screen.getByRole("button", { name: /confirm stop/i }));

    expect(cancelMock.mutate).toHaveBeenCalledTimes(1);
  });

  it("backs out to the Stop state on Keep running, never cancelling", () => {
    render(<StopRunButton runId="r1" status="Running" />);

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    fireEvent.click(screen.getByRole("button", { name: /keep running/i }));

    expect(screen.getByRole("button", { name: /^stop$/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /confirm stop/i })).toBeNull();
    expect(cancelMock.mutate).not.toHaveBeenCalled();
  });

  it("shows a disabled pending state while stopping", () => {
    cancelMock.isPending = true;
    render(<StopRunButton runId="r1" status="Running" />);

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    expect(screen.getByRole("button", { name: /stopping/i })).toBeDisabled();
  });

  it("surfaces an error hint when the stop fails", () => {
    cancelMock.isError = true;
    render(<StopRunButton runId="r1" status="Running" />);

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    expect(screen.getByText(/couldn.t stop/i)).toBeInTheDocument();
  });
});
