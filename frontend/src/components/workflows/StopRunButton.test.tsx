import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// The button drives the cancel mutation + terminal gate through this hook module, and the confirm modal through the
// dialog hook; stub both so the test controls liveness, the mutation state, and the user's confirm/cancel choice.
const cancelMock: { mutate: ReturnType<typeof vi.fn>; isPending: boolean } = { mutate: vi.fn(), isPending: false };
const confirmMock = vi.fn<() => Promise<boolean>>();
vi.mock("@/hooks/use-workflows", () => ({
  useCancelRun: () => cancelMock,
  isRunActive: (s: string) => !["Success", "Failure", "Cancelled"].includes(s),
}));
vi.mock("@/components/dialog", () => ({
  useConfirm: () => confirmMock,
}));

import { StopRunButton } from "./StopRunButton";

beforeEach(() => {
  cancelMock.mutate = vi.fn();
  cancelMock.isPending = false;
  confirmMock.mockReset();
});

describe("StopRunButton", () => {
  it("renders nothing for a terminal run", () => {
    const { container } = render(<StopRunButton runId="r1" status="Success" />);
    expect(container.firstChild).toBeNull();
  });

  it("shows a single Stop button for a live run", () => {
    render(<StopRunButton runId="r1" status="Running" />);
    expect(screen.getByRole("button", { name: /^stop$/i })).toBeInTheDocument();
  });

  it("cancels the run when the confirm modal is accepted", async () => {
    confirmMock.mockResolvedValue(true);
    render(<StopRunButton runId="r1" status="Running" />);

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({ destructive: true }));
    await waitFor(() => expect(cancelMock.mutate).toHaveBeenCalledTimes(1));
  });

  it("does not cancel when the confirm modal is dismissed", async () => {
    confirmMock.mockResolvedValue(false);
    render(<StopRunButton runId="r1" status="Running" />);

    fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

    await waitFor(() => expect(confirmMock).toHaveBeenCalled());
    expect(cancelMock.mutate).not.toHaveBeenCalled();
  });

  it("shows a disabled pending state while stopping", () => {
    cancelMock.isPending = true;
    render(<StopRunButton runId="r1" status="Running" />);

    expect(screen.getByRole("button", { name: /stopping/i })).toBeDisabled();
  });
});
