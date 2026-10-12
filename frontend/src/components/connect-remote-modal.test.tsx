import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam, ProviderInstanceSummary } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import { ConnectRemoteModal } from "@/_imported/ai-code-space/connect-remote-modal";

// Not co-located with the source: vite.config.ts excludes `**/_imported/**` from test discovery.

/**
 * Revoking a team service token takes credentials.manage; disconnecting your OWN personal credential takes nothing
 * beyond membership. The modal must offer exactly that: a Member keeps Disconnect on the Personal tab — which lists
 * only their own credentials — and sees team tokens without a Revoke that could only answer 403.
 *
 * <p>The same for connecting: adding a team token and starting an OAuth connect take credentials.manage, and editing
 * or removing a provider takes repos.manage. Linking a personal access token takes only membership, so "Use a token"
 * stays for everyone.</p>
 */
describe("connect remote modal credential controls", () => {
  const team = (role: MeTeam["role"], permissions: string[]): MeTeam => ({
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role, permissions,
    memberCount: 3, repositoryCount: 0, projectCount: 1, workflowCount: 0,
  });

  const me = (t: MeTeam): MeResponse => ({ id: "u1", email: "u@test.local", name: "Maya", teams: [t], permissions: [], passwordMustChange: false });

  const instance: ProviderInstanceSummary = { id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "https://gitlab.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };

  const credentials = [
    { id: "own", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "Pat", displayName: "Maya's token", status: "Active", createdDate: "2026-01-01T00:00:00Z" },
    { id: "team", teamId: "t1", providerInstanceId: "pi1", ownerUserId: null, ownerUserName: null, ownership: "TeamService", authType: "GroupAccessToken", displayName: "Acme group token", status: "Active", createdDate: "2026-01-01T00:00:00Z" },
  ];

  function renderAs(t: MeTeam, held = credentials) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", t.id);

    const routes: Record<string, unknown> = { "/api/users/me": me(t), "/capabilities": { warnings: [] }, "/api/provider-instances": [instance], "/api/credentials": held };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), { status: body === undefined ? 404 : 200, headers: { "Content-Type": "application/json" } });
    }));

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    return render(
      <QueryClientProvider client={client}>
        <DialogProvider><ConnectRemoteModal onClose={() => {}} /></DialogProvider>
      </QueryClientProvider>,
    );
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it.each([
    ["Viewer", []],
    ["Member", ["workflows.write"]],
  ] as const)("lets a %s disconnect their own credential but offers no revoke on a team token", async (role, permissions) => {
    renderAs(team(role, [...permissions]));

    await waitFor(() => expect(screen.getByRole("button", { name: "Disconnect" })).toBeTruthy());

    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByText("Acme group token")).toBeTruthy());
    expect(screen.queryByRole("button", { name: "Revoke" })).toBeNull();
  });

  it.each([
    ["Viewer", []],
    ["Member", ["workflows.write"]],
  ] as const)("offers a %s a personal token to connect, and nothing that would answer 403", async (role, permissions) => {
    renderAs(team(role, [...permissions]), credentials.filter((c) => c.ownership === "TeamService"));

    await waitFor(() => expect(screen.getByRole("button", { name: /Use a token/ })).toBeTruthy());

    expect(screen.queryByRole("button", { name: /Connect with OAuth/ })).toBeNull();
    expect(screen.queryByTitle("More actions")).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByText("Acme group token")).toBeTruthy());
    expect(screen.queryByRole("button", { name: /Add team token/ })).toBeNull();
  });

  it("gives an admin OAuth connect, the provider menu and a team token to add", async () => {
    renderAs(team("Admin", ["repos.manage", "credentials.manage", "members.manage"]), credentials.filter((c) => c.ownership === "TeamService"));

    await waitFor(() => expect(screen.getByRole("button", { name: /Connect with OAuth/ })).toBeTruthy());

    expect(screen.getByRole("button", { name: /Use a token/ })).toBeTruthy();
    expect(screen.getByTitle("More actions")).toBeTruthy();

    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByRole("button", { name: /Add team token/ })).toBeTruthy());
  });

  it("gives an admin revoke on a team token", async () => {
    renderAs(team("Admin", ["repos.manage", "credentials.manage", "members.manage"]));

    await waitFor(() => expect(screen.getByRole("button", { name: "Disconnect" })).toBeTruthy());

    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Revoke" })).toBeTruthy());
  });
});
