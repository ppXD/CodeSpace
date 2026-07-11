import { Outlet } from "@tanstack/react-router";
import { waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";

// The team-scope layout mounts under _app → AppShell (heavy: sidebar, chat dock, its own queries).
// Stub it to just the outlet so this test exercises ONLY the team gate.
vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

/**
 * The team-scope gate is the tenancy linchpin (PR #1090/#1099): it resolves the URL `teamSlug`
 * (incl. the `personal` alias), points the X-Team-Id header at that team, bounces an unknown slug
 * to the user's first team, and never renders children under a stale team id. These tests drive the
 * REAL route in a memory router, so a regression that mis-resolves or renders early fails here.
 */
describe("team-scope gate", () => {
  const team = (over: Partial<MeTeam>): MeTeam => ({
    id: "t", slug: "s", name: "N", kind: "Workspace", role: "Owner",
    memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0, ...over,
  });
  const personal = team({ id: "p", slug: "personal-a3f8c1d2", kind: "Personal" });
  const acme = team({ id: "a", slug: "acme" });

  const meWith = (...teams: MeTeam[]): MeResponse =>
    ({ id: "u", email: "u@test.local", name: "U", teams, passwordMustChange: false } satisfies MeResponse);

  const backend = (me: MeResponse) => stubFetch({ "/api/users/me": me, "/api/projects": [] });

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("points X-Team-Id at the URL's team and renders it (no redirect)", async () => {
    backend(meWith(personal, acme));

    const { currentPath } = await renderRoute("/teams/acme/projects");

    await waitFor(() => expect(localStorage.getItem("codespace.activeTeamId")).toBe("a"));
    expect(currentPath()).toBe("/teams/acme/projects");
  });

  it("resolves the `personal` alias to the Personal-kind team", async () => {
    backend(meWith(personal, acme));

    await renderRoute("/teams/personal/projects");

    await waitFor(() => expect(localStorage.getItem("codespace.activeTeamId")).toBe("p"));
  });

  it("bounces an unknown slug to the user's first team", async () => {
    backend(meWith(acme, team({ id: "b", slug: "beta" })));

    const { currentPath } = await renderRoute("/teams/does-not-exist/projects");

    await waitFor(() => expect(currentPath()).toBe("/teams/acme/projects"));
  });
});
