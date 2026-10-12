import { Outlet } from "@tanstack/react-router";
import { screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam, ProjectSummary, ProviderInstanceSummary, RepositorySummary } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";

vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

// The app mounts DialogProvider in main.tsx, above the route tree the harness renders; nothing here opens a dialog.
vi.mock("@/components/dialog", async (importOriginal) => ({ ...(await importOriginal<typeof import("@/components/dialog")>()), useConfirm: () => async () => false }));

/**
 * "Remove from this project" is an unbind, "Move to another project" a move, and "Add repository" a bind — each takes
 * repos.manage on the server. Below that tier the controls are absent rather than present-and-refused; the gate reads
 * the server's own expansion of its matrix.
 */
describe("project repositories controls", () => {
  const team = (role: MeTeam["role"], permissions: string[]): MeTeam => ({
    id: "t-1", slug: "platform-team", name: "Platform Team", kind: "Workspace", role, permissions,
    memberCount: 3, repositoryCount: 1, projectCount: 1, workflowCount: 0,
  });

  const me = (t: MeTeam): MeResponse => ({ id: "u-1", email: "u@test.local", name: "Maya", passwordMustChange: false, permissions: [], teams: [t] });

  const project: ProjectSummary = { id: "p-1", teamId: "t-1", slug: "default", name: "Default", createdDate: "2026-01-01T00:00:00Z", activeRepositoryCount: 1, activeVariableCount: 0 };

  const instance: ProviderInstanceSummary = { id: "pi-1", teamId: "t-1", provider: "GitLab", displayName: "GitLab", baseUrl: "https://gitlab.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };

  const repository: RepositorySummary = {
    id: "r-1", teamId: "t-1", providerInstanceId: "pi-1", credentialId: null, fullPath: "acme/api", name: "api", defaultBranch: "main",
    visibility: "Private", status: "Active", webUrl: "https://gitlab.test/acme/api", createdDate: "2026-01-01T00:00:00Z",
  };

  function renderAs(t: MeTeam) {
    stubFetch({ "/api/users/me": me(t), "/api/projects/default": project, "/api/projects": [project], "/api/repositories": [repository], "/api/provider-instances": [instance], "/api/credentials": [] });
    localStorage.setItem("codespace.activeTeamId", t.id);

    return renderRoute("/teams/platform-team/projects/default");
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it.each([
    ["Viewer", []],
    ["Member", ["workflows.write"]],
  ] as const)("offers a %s no way to add, move or remove a repository", async (role, permissions) => {
    await renderAs(team(role, [...permissions]));

    await waitFor(() => expect(screen.getByTitle("Open in provider")).toBeTruthy());

    expect(screen.queryByTitle("Remove from this project")).toBeNull();
    expect(screen.queryByTitle("Move to another project")).toBeNull();
    expect(screen.queryByRole("button", { name: /Add repository/ })).toBeNull();
    expect(screen.getByRole("button", { name: /Connect remote/ })).toBeTruthy();   // a personal token is self-service
  });

  it("gives an admin the add, move and remove controls", async () => {
    await renderAs(team("Admin", ["repos.manage", "credentials.manage", "members.manage"]));

    await waitFor(() => expect(screen.getByTitle("Remove from this project")).toBeTruthy());

    expect(screen.getByTitle("Move to another project")).toBeTruthy();
    expect(screen.getByRole("button", { name: /Add repository/ })).toBeTruthy();
  });
});
