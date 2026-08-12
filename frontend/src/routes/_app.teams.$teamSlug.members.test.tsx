import { Outlet } from "@tanstack/react-router";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";

vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

/**
 * The Members page shell.
 *
 * <p><code>.ct-head</code> is authored with no bottom padding because it expects a
 * <code>.ct-tabs</code> strip to close it out — the tabs bring their own padding and their underline
 * IS the divider. Members had that strip while it was a Settings tab; standing on its own it has to
 * supply the padding itself, exactly as Runs, Agents and Workflows do. Without it the title sits
 * flush on the rule, which is what shipped in #1368.</p>
 */
describe("members page shell", () => {
  const team: MeTeam = {
    id: "t-1", slug: "platform-team", name: "Platform Team", kind: "Workspace",
    role: "Owner", permissions: ["members.manage"], memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0,
  };

  const me: MeResponse = {
    id: "u-1", email: "v@test.local", name: "Viewer", passwordMustChange: false, permissions: [], teams: [team],
  };

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("holds the title off the divider", async () => {
    stubFetch({ "/api/users/me": me, "/api/teams/members": [], "/api/teams/invitations": [] });
    localStorage.setItem("codespace.activeTeamId", team.id);

    const { container } = await renderRoute("/teams/platform-team/members");

    const head = container.querySelector(".ct-head") as HTMLElement;

    expect(head).toBeTruthy();
    expect(head.style.paddingBottom).toBe("18px");
  });
});
