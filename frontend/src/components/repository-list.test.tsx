import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam, ProviderInstanceSummary, RepositorySummary } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import { RepositoryListPage } from "@/_imported/ai-code-space/content";

// Not co-located with the source: vite.config.ts excludes `**/_imported/**` from test discovery.

/**
 * Adding (a bind), unbinding and re-linking a repository take repos.manage on the server. A control that is shown and then answers 403
 * tells someone they may do a thing and then refuses them, so below that tier the controls are absent — and the gate
 * reads the server's own expansion of its matrix (`me.teams[].permissions`), never a role name.
 */
describe("repository list controls", () => {
  const team = (role: MeTeam["role"], permissions: string[]): MeTeam => ({
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role, permissions,
    memberCount: 3, repositoryCount: 2, projectCount: 1, workflowCount: 0,
  });

  const me = (t: MeTeam): MeResponse => ({ id: "u1", email: "u@test.local", name: "Maya", teams: [t], permissions: [], passwordMustChange: false });

  const instance: ProviderInstanceSummary = { id: "pi1", teamId: "t1", provider: "GitLab", displayName: "GitLab", baseUrl: "https://gitlab.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };

  const repo = (over: Partial<RepositorySummary>): RepositorySummary => ({
    id: "r1", teamId: "t1", providerInstanceId: "pi1", credentialId: "c1", fullPath: "acme/api", name: "api", defaultBranch: "main",
    visibility: "Private", status: "Active", webUrl: "https://gitlab.test/acme/api", createdDate: "2026-01-01T00:00:00Z", ...over,
  });

  const repositories = [
    repo({}),
    repo({ id: "r2", fullPath: "acme/web", name: "web", status: "Error", lastError: "credential revoked" }),
  ];

  const credentials = [
    { id: "c2", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "Pat", displayName: "Maya's token", status: "Active", createdDate: "2026-01-01T00:00:00Z" },
  ];

  function renderAs(t: MeTeam) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", t.id);

    const routes: Record<string, unknown> = { "/api/users/me": me(t), "/api/repositories": repositories, "/api/provider-instances": [instance], "/api/credentials": credentials };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), { status: body === undefined ? 404 : 200, headers: { "Content-Type": "application/json" } });
    }));

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    return render(
      <QueryClientProvider client={client}>
        <DialogProvider>
          <RepositoryListPage tab="all" query="" onTabChange={() => {}} onQueryChange={() => {}} onOpenRepo={() => {}} />
        </DialogProvider>
      </QueryClientProvider>,
    );
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it.each([
    ["Viewer", []],
    ["Member", ["workflows.write"]],
  ] as const)("offers a %s no add, no unbind and no re-link", async (role, permissions) => {
    renderAs(team(role, [...permissions]));

    await waitFor(() => expect(screen.getByText("acme/web")).toBeTruthy());
    await waitFor(() => expect(screen.getAllByTitle("Open in provider")).toHaveLength(2));

    expect(screen.queryByTitle("Unbind")).toBeNull();
    expect(screen.queryByRole("button", { name: /Re-link credential|Connect a credential to fix/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Add repository/ })).toBeNull();
    expect(screen.getByRole("button", { name: /Connect remote/ })).toBeTruthy();   // a personal token is self-service
  });

  it("gives an admin add, unbind on every row and re-link on the broken one", async () => {
    renderAs(team("Admin", ["repos.manage", "credentials.manage", "members.manage"]));

    await waitFor(() => expect(screen.getAllByTitle("Unbind")).toHaveLength(2));

    expect(screen.getByRole("button", { name: /Re-link credential/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /Add repository/ })).toBeTruthy();
  });
});
