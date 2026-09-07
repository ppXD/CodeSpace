import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { tasksApi, type RoutePreviewInput, type TaskRoutePreviewResult } from "@/api/tasks";
import { ROUTE_PREVIEW_DEBOUNCE_MS, useRoutePreview } from "./use-route-preview";

vi.mock("@/api/tasks", () => ({ tasksApi: { routePreview: vi.fn() } }));
const input: RoutePreviewInput = { taskText: "Explain this contract", surfaceKind: "chat", effort: "auto", autonomy: "Confined" };
const reply = (id: string, ttl = 60_000): TaskRoutePreviewResult => ({ routeSnapshotId: id, createdAt: new Date(Date.now()).toISOString(), expiresAt: new Date(Date.now() + ttl).toISOString(), route: { projectionKind: "single-agent" } as TaskRoutePreviewResult["route"], deploymentAutonomyCeiling: "Standard" });

beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(new Date("2026-09-07T00:00:00Z")); vi.mocked(tasksApi.routePreview).mockReset(); });
afterEach(() => { vi.useRealTimers(); });

const settle = () => act(async () => { await vi.advanceTimersByTimeAsync(ROUTE_PREVIEW_DEBOUNCE_MS); });

describe("route snapshot references", () => {
  it("never exposes a reference for changed controls or an out-of-order response", async () => {
    let resolveFirst!: (value: TaskRoutePreviewResult) => void;
    vi.mocked(tasksApi.routePreview).mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve; })).mockResolvedValueOnce(reply("current"));
    const hook = renderHook(({ value }) => useRoutePreview(value), { initialProps: { value: input } });
    await settle();
    hook.rerender({ value: { ...input, timeoutSeconds: 30 } });
    expect(hook.result.current.routeSnapshotId).toBeUndefined();
    expect(hook.result.current.answered).toBe(false);
    await settle();
    expect(hook.result.current.routeSnapshotId).toBe("current");
    await act(async () => { resolveFirst(reply("stale")); });
    expect(hook.result.current.routeSnapshotId).toBe("current");
    hook.unmount();
  });

  it("invalidates acceptance compatibility with controls and never revives it from a stale response", async () => {
    const capable: TaskRoutePreviewResult = { ...reply("capable"), acceptanceCompatibility: { state: "Compatible", projectionKind: "single-agent", detail: "Current input adapter" } };
    let resolveStale!: (value: TaskRoutePreviewResult) => void;
    vi.mocked(tasksApi.routePreview).mockResolvedValueOnce(capable).mockImplementationOnce(() => new Promise(resolve => { resolveStale = resolve; })).mockResolvedValueOnce(reply("current-unknown"));
    const hook = renderHook(({ value }) => useRoutePreview(value), { initialProps: { value: input } });
    await settle();
    expect(hook.result.current.acceptanceCompatibility?.state).toBe("Compatible");
    hook.rerender({ value: { ...input, repositoryId: "other-repo" } });
    expect(hook.result.current.acceptanceCompatibility).toBeNull();
    await settle();
    hook.rerender({ value: { ...input, effort: "standard" } });
    await settle();
    await act(async () => { resolveStale(capable); });
    expect(hook.result.current.routeSnapshotId).toBe("current-unknown");
    expect(hook.result.current.acceptanceCompatibility).toBeNull();
    hook.unmount();
  });

  it("refreshes an expired reference before treating the current route as answered", async () => {
    vi.mocked(tasksApi.routePreview).mockImplementationOnce(async () => reply("expired", 2_000)).mockImplementationOnce(async () => reply("renewed"));
    const hook = renderHook(() => useRoutePreview(input));
    await settle();
    expect(hook.result.current.routeSnapshotId).toBe("expired");
    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(hook.result.current.routeSnapshotId).toBeUndefined();
    expect(hook.result.current.answered).toBe(false);
    await settle();
    expect(hook.result.current.routeSnapshotId).toBe("renewed");
    expect(tasksApi.routePreview).toHaveBeenCalledTimes(2);
    hook.unmount();
  });

  it("starts a new admission intent only after acknowledging success for the current reference", async () => {
    vi.mocked(tasksApi.routePreview).mockResolvedValueOnce(reply("first")).mockResolvedValueOnce(reply("next"));
    const hook = renderHook(() => useRoutePreview(input));
    await settle();
    act(() => hook.result.current.releaseReference("unrelated"));
    expect(hook.result.current.routeSnapshotId).toBe("first");
    act(() => hook.result.current.releaseReference("first"));
    expect(hook.result.current.routeSnapshotId).toBeUndefined();
    expect(hook.result.current.answered).toBe(false);
    await settle();
    expect(hook.result.current.routeSnapshotId).toBe("next");
    act(() => hook.result.current.releaseReference("first"));
    expect(hook.result.current.routeSnapshotId).toBe("next");
    hook.unmount();
  });

  it("uses server lifetime rather than the browser wall clock to schedule refresh", async () => {
    const serverReply = reply("server-time", 2_000);
    vi.setSystemTime(new Date("2030-01-01T00:00:00Z"));
    vi.mocked(tasksApi.routePreview).mockResolvedValueOnce(serverReply);
    const hook = renderHook(() => useRoutePreview(input));
    await settle();
    expect(hook.result.current.routeSnapshotId).toBe("server-time");
    expect(tasksApi.routePreview).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1_000); });
    expect(hook.result.current.routeSnapshotId).toBe("server-time");
    hook.unmount();
  });

  it("keeps an uncertain launch reference through expiry so retry cannot silently create a second run", async () => {
    vi.mocked(tasksApi.routePreview).mockResolvedValueOnce(reply("attempted", 2_000));
    const hook = renderHook(() => useRoutePreview(input));
    await settle();
    act(() => hook.result.current.markLaunchAttempt("attempted"));
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });
    expect(hook.result.current.routeSnapshotId).toBe("attempted");
    expect(hook.result.current.answered).toBe(true);
    expect(tasksApi.routePreview).toHaveBeenCalledTimes(1);
    hook.unmount();
  });

  it("does not retain a reference after a failed refresh or when preview is disabled", async () => {
    vi.mocked(tasksApi.routePreview).mockResolvedValueOnce(reply("previous")).mockRejectedValueOnce(new Error("offline"));
    const hook = renderHook(({ value }: { value: RoutePreviewInput | null }) => useRoutePreview(value), { initialProps: { value: input as RoutePreviewInput | null } });
    await settle();
    hook.rerender({ value: { ...input, pushBranch: true } });
    await settle();
    expect(hook.result.current.failed).toBe(true);
    expect(hook.result.current.routeSnapshotId).toBeUndefined();
    hook.rerender({ value: null });
    expect(hook.result.current.routeSnapshotId).toBeUndefined();
    expect(hook.result.current.answered).toBe(true);
    hook.unmount();
  });
});
