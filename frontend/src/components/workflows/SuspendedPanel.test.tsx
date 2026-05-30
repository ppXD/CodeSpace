import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { WorkflowRunWaitInfo } from "@/api/workflows";
import { SuspendedPanel } from "./RunDetailView";

/**
 * SuspendedPanel is the resume affordance shown on a Suspended run. These pin the behaviour the
 * human-in-the-loop flow depends on:
 *   1. an Approval wait renders the prompt + Approve/Reject;
 *   2. Approve / Reject post the correct decision (with the optional comment);
 *   3. a Timer wait shows a wake hint, NOT approval buttons.
 */

const { mutate } = vi.hoisted(() => ({ mutate: vi.fn() }));

vi.mock("@/hooks/use-workflows", () => ({
  useResumeRun: () => ({ mutate, isPending: false, isError: false }),
}));

function approvalWait(prompt: string): WorkflowRunWaitInfo {
  return { nodeId: "approval", kind: "Approval", payload: { prompt } };
}

describe("SuspendedPanel", () => {
  it("approves with the typed comment", () => {
    render(<SuspendedPanel runId="run-1" wait={approvalWait("Deploy to production?")} />);

    expect(screen.getByText("Deploy to production?")).toBeTruthy();

    fireEvent.change(screen.getByPlaceholderText("Comment (optional)"), { target: { value: "  ship it  " } });
    fireEvent.click(screen.getByText("Approve"));

    expect(mutate).toHaveBeenCalledWith({ approved: true, comment: "ship it" });
  });

  it("rejects, and omits an empty comment", () => {
    mutate.mockClear();
    render(<SuspendedPanel runId="run-1" wait={approvalWait("OK?")} />);

    fireEvent.click(screen.getByText("Reject"));

    expect(mutate).toHaveBeenCalledWith({ approved: false, comment: undefined });
  });

  it("shows a wake hint for a Timer wait — no approval buttons", () => {
    const wait: WorkflowRunWaitInfo = { nodeId: "sleep", kind: "Timer", wakeAt: "2026-05-30T11:00:00Z", payload: {} };
    render(<SuspendedPanel runId="run-1" wait={wait} />);

    expect(screen.queryByText("Approve")).toBeNull();
    expect(screen.getByText(/Resumes around/)).toBeTruthy();
  });
});
