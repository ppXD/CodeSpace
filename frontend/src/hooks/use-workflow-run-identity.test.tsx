import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { getRun, getRunIdentity } = vi.hoisted(() => ({ getRun: vi.fn(), getRunIdentity: vi.fn() }));
vi.mock("@/api/workflows", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/workflows")>();
  return { ...original, workflowsApi: { ...original.workflowsApi, getRun, getRunIdentity } };
});

import { WORKFLOW_RUN_IDENTITY_POLL_MS, useWorkflowRunIdentity } from "./use-workflows";

function wrapper(client: QueryClient) {
  return function QueryWrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

async function settle() {
  await act(async () => {
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(0);
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  getRun.mockReset();
  getRunIdentity.mockReset();
});

afterEach(() => vi.useRealTimers());

describe("useWorkflowRunIdentity", () => {
  it("issues no identity request while the Canvas metadata query owns status", async () => {
    renderHook(() => useWorkflowRunIdentity("42", false), { wrapper: wrapper(new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })) });
    await settle();
    expect(getRunIdentity).not.toHaveBeenCalled();
  });

  it("polls only the identity endpoint while active and stops on the terminal identity", async () => {
    getRunIdentity
      .mockResolvedValueOnce({ id: "11111111-1111-1111-1111-111111111111", runNumber: 42, status: "Running" })
      .mockResolvedValueOnce({ id: "11111111-1111-1111-1111-111111111111", runNumber: 42, status: "Success" });
    const { result } = renderHook(() => useWorkflowRunIdentity("42"), { wrapper: wrapper(new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })) });
    await settle();

    expect(result.current.data?.status).toBe("Running");
    expect(getRunIdentity).toHaveBeenCalledExactlyOnceWith("42", expect.any(AbortSignal));

    await act(() => vi.advanceTimersByTimeAsync(WORKFLOW_RUN_IDENTITY_POLL_MS));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(WORKFLOW_RUN_IDENTITY_POLL_MS * 4));

    expect(result.current.data?.status).toBe("Success");
    expect(getRunIdentity).toHaveBeenCalledTimes(2);
    expect(getRun).not.toHaveBeenCalled();
  });

  it("aborts the identity read when its route ref changes", async () => {
    getRunIdentity.mockImplementation(() => new Promise(() => {}));
    const { rerender, unmount } = renderHook(({ ref }) => useWorkflowRunIdentity(ref), { initialProps: { ref: "41" }, wrapper: wrapper(new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })) });
    await settle();
    const first = getRunIdentity.mock.calls[0][1] as AbortSignal;

    rerender({ ref: "42" });
    await settle();
    expect(first.aborted).toBe(true);
    const second = getRunIdentity.mock.calls[1][1] as AbortSignal;

    unmount();
    expect(second.aborted).toBe(true);
  });
});
