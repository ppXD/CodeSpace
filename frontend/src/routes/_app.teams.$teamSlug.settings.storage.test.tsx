import { Outlet } from "@tanstack/react-router";
import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";

vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

describe("storage settings route", () => {
  const team: MeTeam = {
    id: "t-1", slug: "platform-team", name: "Platform Team", kind: "Workspace",
    role: "Owner", permissions: [], memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0,
  };

  const me: MeResponse = {
    id: "u-1", email: "owner@test.local", name: "Owner", passwordMustChange: false, permissions: [], teams: [team],
  };

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("is a first-class Settings tab and does not redirect to another section", async () => {
    stubFetch({ "/api/users/me": me, "/api/storage/routes/page": { items: [], nextCursor: null } });
    localStorage.setItem("codespace.activeTeamId", team.id);

    const { currentPath } = await renderRoute("/teams/platform-team/settings/storage");

    expect(currentPath()).toBe("/teams/platform-team/settings/storage");
    expect(screen.getByRole("tab", { name: "Storage" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("heading", { name: "Artifact storage" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Data routing" })).toBeTruthy();
  });
});
