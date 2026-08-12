import { Outlet } from "@tanstack/react-router";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";
import { Sidebar } from "@/_imported/ai-code-space/sidebar";

// Not co-located with the source: vite.config.ts excludes `**/_imported/**` from test discovery.
// Same arrangement as src/lib/pager.test.tsx, which covers another component from that folder.
//
// The sidebar reads the router (active-nav highlight, navigation) and the /me query, so it is
// mounted where the app mounts it — inside the real route tree, standing in for the shell.
vi.mock("@/components/AppShell", () => ({ AppShell: () => <><Sidebar /><Outlet /></> }));

/**
 * The team switcher's footer.
 *
 * <p>The footer is nothing but the frame around "Create workspace" — it carries the divider and the
 * padding, and holds no content of its own. Gating only the action inside it left an empty frame
 * behind, drawing a rule under the last team with dead space below it, for every account that does
 * not hold <code>teams.create</code>. So the gate belongs on the frame.</p>
 */
describe("sidebar team switcher", () => {
  const team: MeTeam = {
    id: "t-1", slug: "platform-team", name: "Platform Team", kind: "Workspace",
    role: "Owner", permissions: [], memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0,
  };

  const me = (permissions: string[]): MeResponse => ({
    id: "u-1", email: "v@test.local", name: "Viewer", passwordMustChange: false, permissions, teams: [team],
  });

  async function openSwitcher(permissions: string[]) {
    stubFetch({ "/api/users/me": me(permissions), "/api/teams/members": [], "/api/teams/invitations": [] });
    localStorage.setItem("codespace.activeTeamId", team.id);

    await renderRoute("/teams/platform-team/members");

    await waitFor(() => expect(screen.getAllByText("Platform Team").length).toBeGreaterThan(0));

    // The popover portals to document.body, so query the document rather than the container.
    fireEvent.click(document.querySelector(".sb-ws") as HTMLElement);

    await waitFor(() => expect(document.querySelector(".sb-pop")).toBeTruthy());
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("ends after the last team when this account may not create one", async () => {
    await openSwitcher([]);

    expect(screen.queryByText("Create workspace")).toBeNull();

    // Not "the action is hidden" — the FRAME must be gone too. An empty .sb-pop-foot still paints
    // its border-top and padding, which is the line under the last team the user reported.
    expect(document.querySelector(".sb-pop-foot")).toBeNull();
  });

  it("keeps the footer when this account may create one", async () => {
    await openSwitcher(["teams.create"]);

    expect(screen.getByText("Create workspace")).toBeTruthy();
    expect(document.querySelector(".sb-pop-foot")).toBeTruthy();
  });
});
