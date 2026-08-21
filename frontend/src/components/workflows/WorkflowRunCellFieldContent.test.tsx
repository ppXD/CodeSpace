import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunCellFieldRangePage, WorkflowRunCellFieldReadIdentity } from "@/api/workflowRunCellFieldRangeApi";

const { read } = vi.hoisted(() => ({ read: vi.fn() }));
vi.mock("@/api/workflowRunCellFieldRangeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/workflowRunCellFieldRangeApi")>();
  return { ...original, workflowRunCellFieldRangeApi: { read } };
});

import { WorkflowRunCellFieldContent } from "./WorkflowRunCellFieldContent";

const identity: WorkflowRunCellFieldReadIdentity = {
  requestedRunId: "11111111-1111-4111-8111-111111111111",
  scope: "LineageMerged",
  sourceRunId: "22222222-2222-4222-8222-222222222222",
  nodeId: "worker",
  iterationKey: "",
  stateRecordId: "33333333-3333-4333-8333-333333333333",
  stateRecordSequence: 42,
  firstStartedRecordId: "44444444-4444-4444-8444-444444444444",
  firstStartedRecordSequence: 17,
  section: "Output",
  name: "result",
};

function page(index: number, count: number, text: string): WorkflowRunCellFieldRangePage {
  const offset = index * 64 * 1024;
  const returned = new TextEncoder().encode(text).byteLength;
  return {
    ...identity,
    status: "Success",
    availability: "Available",
    source: "Inline",
    requestCursor: index === 0 ? null : `cursor-${index}`,
    limitBytes: 64 * 1024,
    offsetBytes: offset,
    returnedBytes: returned,
    totalBytes: count * 64 * 1024,
    nextCursor: index + 1 < count ? `cursor-${index + 1}` : null,
    text,
    contentType: "application/json",
    integrityVerified: true,
    completeJsonValue: false,
    retryable: false,
  };
}

function completeJson(value: string): WorkflowRunCellFieldRangePage {
  const bytes = new TextEncoder().encode(value).byteLength;
  return { ...page(0, 1, value), returnedBytes: bytes, totalBytes: bytes, completeJsonValue: true };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

afterEach(() => {
  read.mockReset();
  vi.unstubAllGlobals();
});

describe("WorkflowRunCellFieldContent", () => {
  it("performs zero byte reads until the descriptor is actually expanded and keeps bytes outside React Query", async () => {
    read.mockResolvedValue(completeJson('{"ok":true}'));
    const view = render(<WorkflowRunCellFieldContent identity={identity} expanded={false} />);

    expect(read).not.toHaveBeenCalled();
    view.rerender(<WorkflowRunCellFieldContent identity={identity} expanded />);

    expect(await screen.findByText('"ok"')).toBeInTheDocument();
    expect(read).toHaveBeenCalledExactlyOnceWith(identity, { cursor: null, offsetBytes: 0 }, expect.any(AbortSignal));
    view.rerender(<WorkflowRunCellFieldContent identity={{ ...identity }} expanded />);
    expect(read).toHaveBeenCalledTimes(1);
    const sources = import.meta.glob(["../../hooks/use-workflow-run-cell-field-content.ts", "./WorkflowRunCellFieldContent.tsx"],
      { eager: true, import: "default", query: "?raw" }) as Record<string, string>;
    expect(Object.values(sources).join("\n")).not.toMatch(/react-query|useQuery|QueryClient/i);
  });

  it("shows only a complete 0..EOF value in JsonView and leaves partial structured text as a UTF-8 window", async () => {
    read.mockResolvedValueOnce(completeJson('{"nested":{"value":7}}'));
    const view = render(<WorkflowRunCellFieldContent identity={identity} expanded />);
    expect(await screen.findByText('"nested"')).toBeInTheDocument();
    expect(view.container.querySelector(".wf-jsonv")).toBeInTheDocument();
    expect(view.container.querySelector("pre")).not.toBeInTheDocument();

    read.mockResolvedValueOnce(page(0, 2, '{"nested":'));
    view.rerender(<WorkflowRunCellFieldContent identity={{ ...identity, name: "partial" }} expanded />);
    await waitFor(() => expect(view.container.querySelector("pre")).toHaveTextContent('{"nested":'));
    expect(view.container.querySelector(".wf-jsonv")).not.toBeInTheDocument();
  });

  it("loads pages only by hand, caps local and DOM content at eight pages, and marks earlier bytes omitted", async () => {
    const chunks = Array.from({ length: 9 }, (_, index) => String.fromCharCode(65 + index).repeat(64 * 1024));
    read.mockImplementation((_identity, request: { offsetBytes: number }) => {
      const index = request.offsetBytes / (64 * 1024);
      return Promise.resolve(page(index, chunks.length, chunks[index]));
    });
    const view = render(<WorkflowRunCellFieldContent identity={identity} expanded />);
    await waitFor(() => expect(view.container.querySelector("pre")?.textContent).toHaveLength(64 * 1024));

    for (let index = 1; index < chunks.length; index += 1) {
      fireEvent.click(screen.getByRole("button", { name: "Load next page" }));
      await waitFor(() => expect(read).toHaveBeenCalledTimes(index + 1));
      await waitFor(() => expect(screen.getByText(`Page ${index + 1} of at least ${index + 1}`)).toBeInTheDocument());
    }

    const visible = view.container.querySelector("pre")!.textContent!;
    expect(new TextEncoder().encode(visible).byteLength).toBe(8 * 64 * 1024);
    expect(visible.startsWith("B")).toBe(true);
    expect(screen.getByText("Earlier field bytes were omitted from this 512 KiB local window.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Return to start" })).toBeInTheDocument();
  });

  it("labels an unverified artifact window instead of implying end-to-end integrity", async () => {
    read.mockResolvedValue({ ...page(0, 2, "partial"), source: "Artifact", integrityVerified: false });
    render(<WorkflowRunCellFieldContent identity={identity} expanded />);

    expect(await screen.findByText("This artifact byte window is not end-to-end integrity verified.")).toBeInTheDocument();
  });

  it("preserves prior healthy pages when a continuation backend is temporarily unavailable", async () => {
    const first = page(0, 2, "A".repeat(64 * 1024));
    const unavailable: WorkflowRunCellFieldRangePage = {
      ...page(1, 2, ""), availability: "BackendUnavailable", source: "Artifact", returnedBytes: 0, totalBytes: null,
      text: null, contentType: "application/json", integrityVerified: false, completeJsonValue: false, retryable: true,
    };
    read.mockResolvedValueOnce(first).mockResolvedValueOnce(unavailable);
    const view = render(<WorkflowRunCellFieldContent identity={identity} expanded />);
    await waitFor(() => expect(view.container.querySelector("pre")?.textContent).toHaveLength(64 * 1024));

    fireEvent.click(screen.getByRole("button", { name: "Load next page" }));

    expect(await screen.findByText("The field storage backend is unavailable.")).toBeInTheDocument();
    expect(view.container.querySelector("pre")?.textContent).toHaveLength(64 * 1024);
    expect(screen.getByRole("button", { name: "Retry field content" })).toBeInTheDocument();
  });

  it("offers retry only for typed BackendUnavailable and never for integrity or contract failures", async () => {
    const failure = (availability: "BackendUnavailable" | "IntegrityFailure", retryable: boolean): WorkflowRunCellFieldRangePage => ({
      ...page(0, 1, ""), availability, source: "Artifact", returnedBytes: 0, totalBytes: null, text: null,
      contentType: null, integrityVerified: false, completeJsonValue: false, retryable,
    });
    read.mockResolvedValueOnce(failure("BackendUnavailable", true)).mockResolvedValueOnce(completeJson("1"));
    const view = render(<WorkflowRunCellFieldContent identity={identity} expanded />);
    expect(await screen.findByRole("button", { name: "Retry field content" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry field content" }));
    await waitFor(() => expect(view.container.querySelector(".wf-jsonv")).toBeInTheDocument());

    read.mockResolvedValueOnce(failure("IntegrityFailure", false));
    view.rerender(<WorkflowRunCellFieldContent identity={{ ...identity, name: "broken" }} expanded />);
    expect(await screen.findByText("Field content failed its integrity checks.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retry field content" })).not.toBeInTheDocument();
  });

  it("aborts close, unmount and identity switches and generation-fences stale replies", async () => {
    const first = deferred<WorkflowRunCellFieldRangePage | null>();
    const second = deferred<WorkflowRunCellFieldRangePage | null>();
    read.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const view = render(<WorkflowRunCellFieldContent identity={identity} expanded />);
    await waitFor(() => expect(read).toHaveBeenCalledTimes(1));
    const firstSignal = read.mock.calls[0][2] as AbortSignal;

    const secondIdentity = { ...identity, name: "second" };
    view.rerender(<WorkflowRunCellFieldContent identity={secondIdentity} expanded />);
    await waitFor(() => expect(read).toHaveBeenCalledTimes(2));
    expect(firstSignal.aborted).toBe(true);
    first.resolve(completeJson('{"stale":true}'));
    second.resolve({ ...completeJson('{"fresh":true}'), name: "second" });
    expect(await screen.findByText('"fresh"')).toBeInTheDocument();
    expect(screen.queryByText('"stale"')).not.toBeInTheDocument();

    view.rerender(<WorkflowRunCellFieldContent identity={secondIdentity} expanded={false} />);
    expect((read.mock.calls[1][2] as AbortSignal).aborted).toBe(true);

    const third = deferred<WorkflowRunCellFieldRangePage | null>();
    read.mockReturnValueOnce(third.promise);
    view.rerender(<WorkflowRunCellFieldContent identity={secondIdentity} expanded />);
    await waitFor(() => expect(read).toHaveBeenCalledTimes(3));
    const thirdSignal = read.mock.calls[2][2] as AbortSignal;
    act(() => view.unmount());
    expect(thirdSignal.aborted).toBe(true);
  });
});
