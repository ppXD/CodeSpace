import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ProviderInstanceSummary } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import { ProvidersSettings } from "@/components/settings/ProvidersSettings";

/**
 * The Providers summary agrees with the modal it opens: a provider the person has a working credential on is connected, one
 * whose credential the provider refused says so — the modal lists that credential with the way to bring it back — and only a
 * provider with nothing at all reads as not connected.
 */
describe("providers settings summary", () => {
  const instance: ProviderInstanceSummary = { id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "https://gitlab.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };
  const credential = (id: string, extra: Record<string, unknown> = {}) => ({ id, teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "Pat", displayName: `Maya's ${id}`, status: "Active", createdDate: "2026-01-01T00:00:00Z", ...extra });

  function renderWith(held: unknown[]) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", "t1");

    const routes: Record<string, unknown> = {
      "/api/users/me": { id: "u1", email: "u@test.local", name: "Maya", teams: [], permissions: [], passwordMustChange: false },
      "/api/provider-instances": [instance],
      "/api/credentials": held,
    };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), { status: body === undefined ? 404 : 200, headers: { "Content-Type": "application/json" } });
    }));

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    render(
      <QueryClientProvider client={client}>
        <DialogProvider><ProvidersSettings /></DialogProvider>
      </QueryClientProvider>,
    );
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it.each([
    { what: "a working credential", held: [credential("ok")], expected: "connected" },
    { what: "a credential the provider refused", held: [credential("dead", { status: "Error" })], expected: "needs reconnect" },
    { what: "a token whose expiry passed, though its row is still Active", held: [credential("lapsed", { health: "rejected" })], expected: "needs reconnect" },
    { what: "a refused one beside a working one", held: [credential("dead", { status: "Error" }), credential("ok")], expected: "connected" },
    { what: "only someone else's credential", held: [credential("theirs", { ownerUserId: "u2" })], expected: "not connected" },
    { what: "nothing", held: [], expected: "not connected" },
  ])("reads $what as '$expected'", async ({ held, expected }) => {
    renderWith(held);

    await waitFor(() => expect(screen.getByText("Acme GitLab")).toBeTruthy());
    await waitFor(() => expect(screen.getByText(expected)).toBeTruthy());

    for (const other of ["connected", "needs reconnect", "not connected"].filter((label) => label !== expected)) expect(screen.queryByText(other)).toBeNull();
  });
});
