import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const specPreviewSpy = vi.fn();
vi.mock("@/api/tasks", () => ({
  tasksApi: { specPreview: (input: { goal: string; repositoryId?: string }) => specPreviewSpy(input) },
}));

import { SPEC_PREVIEW_DEBOUNCE_MS, SPEC_PREVIEW_MIN_GOAL_LENGTH, useSpecPreview } from "./use-spec-preview";

const SUGGESTION = { acceptanceChecks: ["dotnet", "test"], acceptanceCriteria: ["green"], rationale: "r", confidence: 0.8 };

beforeEach(() => {
  vi.useFakeTimers();
  specPreviewSpy.mockReset();
  specPreviewSpy.mockResolvedValue({ suggestion: SUGGESTION, grounded: true });
});
afterEach(() => vi.useRealTimers());

describe("useSpecPreview", () => {
  it("fires once after the debounce with the trimmed goal and repo", async () => {
    const { result } = renderHook(() => useSpecPreview("  Fix the parser crash  ", "repo-1"));

    expect(specPreviewSpy).not.toHaveBeenCalled();
    await act(() => vi.advanceTimersByTimeAsync(SPEC_PREVIEW_DEBOUNCE_MS));

    expect(specPreviewSpy).toHaveBeenCalledExactlyOnceWith({ goal: "Fix the parser crash", repositoryId: "repo-1" });
    expect(result.current.suggestion).toEqual(SUGGESTION);
    expect(result.current.grounded).toBe(true);
    expect(result.current.loading).toBe(false);
  });

  it("a goal below the floor never calls and clears any prior suggestion", async () => {
    const { result, rerender } = renderHook(({ goal }) => useSpecPreview(goal, undefined), { initialProps: { goal: "Fix the parser crash" } });
    await act(() => vi.advanceTimersByTimeAsync(SPEC_PREVIEW_DEBOUNCE_MS));
    expect(result.current.suggestion).toEqual(SUGGESTION);

    rerender({ goal: "x".repeat(SPEC_PREVIEW_MIN_GOAL_LENGTH - 1) });
    expect(result.current.suggestion).toBeNull();
    await act(() => vi.advanceTimersByTimeAsync(SPEC_PREVIEW_DEBOUNCE_MS));
    expect(specPreviewSpy).toHaveBeenCalledTimes(1);
  });

  it("keystrokes inside the debounce window collapse to one call", async () => {
    const { rerender } = renderHook(({ goal }) => useSpecPreview(goal, undefined), { initialProps: { goal: "Fix the parser cra" } });
    await act(() => vi.advanceTimersByTimeAsync(SPEC_PREVIEW_DEBOUNCE_MS / 2));
    rerender({ goal: "Fix the parser crash" });
    await act(() => vi.advanceTimersByTimeAsync(SPEC_PREVIEW_DEBOUNCE_MS));

    expect(specPreviewSpy).toHaveBeenCalledExactlyOnceWith({ goal: "Fix the parser crash", repositoryId: undefined });
  });

  it("a stale in-flight reply is dropped when the goal moves on", async () => {
    let resolveFirst!: (v: unknown) => void;
    specPreviewSpy.mockImplementationOnce(() => new Promise(r => { resolveFirst = r; }));

    const { result, rerender } = renderHook(({ goal }) => useSpecPreview(goal, undefined), { initialProps: { goal: "Fix the parser crash" } });
    await act(() => vi.advanceTimersByTimeAsync(SPEC_PREVIEW_DEBOUNCE_MS));

    rerender({ goal: "Write the migration guide" });
    await act(() => vi.advanceTimersByTimeAsync(SPEC_PREVIEW_DEBOUNCE_MS));
    expect(result.current.suggestion).toEqual(SUGGESTION);

    await act(async () => { resolveFirst({ suggestion: { ...SUGGESTION, rationale: "stale" }, grounded: false }); });
    expect(result.current.suggestion).toEqual(SUGGESTION);
  });

  it("a transport fault degrades to a null suggestion without surfacing", async () => {
    specPreviewSpy.mockRejectedValueOnce(new Error("boom"));
    const { result } = renderHook(() => useSpecPreview("Fix the parser crash", undefined));

    await act(() => vi.advanceTimersByTimeAsync(SPEC_PREVIEW_DEBOUNCE_MS));

    expect(result.current.suggestion).toBeNull();
    expect(result.current.loading).toBe(false);
  });
});
