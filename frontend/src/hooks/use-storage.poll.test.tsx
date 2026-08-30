import { QueryClient, QueryClientProvider, type UseQueryOptions } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

vi.mock("@/api/storage", () => ({
  storageApi: {
    getPlacementIntegrity: vi.fn().mockResolvedValue({ missing: 0, corrupt: 0, available: 0, oldestVerifiedAt: null }),
    listProfilePage: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
  },
}));

import { STORAGE_OBSERVATION_POLL_MS, STORAGE_PLACEMENT_INTEGRITY_KEY, STORAGE_PROFILES_KEY, usePlacementIntegrity, useStorageProfiles } from "./use-storage";

/**
 * An OPEN storage page has to notice what the background sweeps found. This app disables refetch-on-focus globally
 * and staleTime never initiates a request by itself, so without a real interval the health badge and the integrity
 * line freeze at whatever was true when the page mounted — "red within fifteen minutes" would be a claim about the
 * database, not about anything an operator can see.
 */
describe("storage observation polling", () => {
  const mount = <T,>(hook: () => T) => {
    const client = new QueryClient({ defaultOptions: { queries: { refetchOnWindowFocus: false, retry: false } } });
    const wrapper = ({ children }: { children: React.ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
    const rendered = renderHook(hook, { wrapper });
    return { client, rendered };
  };

  it("keeps asking about placement integrity while the page is open", async () => {
    const { client, rendered } = mount(() => usePlacementIntegrity());
    await waitFor(() => expect(rendered.result.current.isSuccess).toBe(true));

    // The cache's static option type is narrower than what the observer actually stored; the cast reads the
    // runtime value the hook registered, which is exactly what the mutation check needs to see disappear.
    const query = client.getQueryCache().find({ queryKey: STORAGE_PLACEMENT_INTEGRITY_KEY });
    expect((query?.options as UseQueryOptions).refetchInterval).toBe(STORAGE_OBSERVATION_POLL_MS);
  });

  it("keeps asking about profile health while the page is open", async () => {
    const { client, rendered } = mount(() => useStorageProfiles());
    await waitFor(() => expect(rendered.result.current.isSuccess).toBe(true));

    const query = client.getQueryCache().find({ queryKey: STORAGE_PROFILES_KEY });
    expect((query?.options as UseQueryOptions).refetchInterval).toBe(STORAGE_OBSERVATION_POLL_MS);
  });

  it("polls well inside the fifteen-minute detection promise", () => {
    // The backend probe runs every fifteen minutes against a ten-minute staleness window; a poll slower than that
    // window would make the page the bottleneck of its own promise.
    expect(STORAGE_OBSERVATION_POLL_MS).toBeLessThanOrEqual(5 * 60_000);
  });
});
