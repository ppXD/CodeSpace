import { fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute } from "@/test/route-harness";
import { CreateWorkspaceDialog } from "./CreateWorkspaceDialog";

// The dialog opens from the sidebar, which lives in the shell. Standing in for the shell puts the
// dialog inside the real router, so the navigation it performs is the real one.
vi.mock("@/components/AppShell", () => ({ AppShell: () => <CreateWorkspaceDialog onClose={() => {}} /> }));

/**
 * Creating a workspace, from the name typed to the team landed in.
 *
 * <p>The navigation is the part worth exercising against the real route tree: the destination is
 * gated on the new team appearing in <code>/me</code>, so a dialog that navigates before the cache
 * has caught up bounces the person who just created it straight back out.</p>
 */
describe("create workspace dialog", () => {
  const team = (over: Partial<MeTeam> = {}): MeTeam => ({
    id: "t-1", slug: "platform-team", name: "Platform Team", kind: "Workspace",
    role: "Owner", permissions: [], memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0,
    ...over,
  });

  /** /me answers with the created team included, the way it does once the mutation has invalidated it. */
  function stubBackend() {
    const created = { id: "t-2", slug: "design-guild", name: "Design Guild", kind: "Workspace" as const };
    const posts: { url: string; body: string }[] = [];

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();

      if (init?.method === "POST" && url.includes("/api/teams")) {
        posts.push({ url, body: String(init.body) });
        return new Response(JSON.stringify(created), { status: 200, headers: { "Content-Type": "application/json" } });
      }

      if (url.includes("/api/users/me")) {
        const me: MeResponse = {
          id: "u-1", email: "v@test.local", name: "Viewer", passwordMustChange: false, permissions: ["teams.create"],
          teams: posts.length === 0 ? [team()] : [team(), team({ id: created.id, slug: created.slug, name: created.name })],
        };
        return new Response(JSON.stringify(me), { status: 200, headers: { "Content-Type": "application/json" } });
      }

      return new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } });
    }));

    return { posts, created };
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("will not create a workspace with no name", async () => {
    stubBackend();
    localStorage.setItem("codespace.activeTeamId", "t-1");

    await renderRoute("/teams/platform-team/projects");

    await waitFor(() => expect(screen.getByRole("dialog", { name: "Create workspace" })).toBeTruthy());

    expect(screen.getByRole("button", { name: "Create" }).hasAttribute("disabled")).toBe(true);

    // Whitespace is not a name — the server would reject it, and letting the button light up invites
    // finding that out the hard way.
    fireEvent.change(screen.getByRole("textbox"), { target: { value: "   " } });

    expect(screen.getByRole("button", { name: "Create" }).hasAttribute("disabled")).toBe(true);
  });

  it("creates the workspace and lands in it", async () => {
    const { posts } = stubBackend();
    localStorage.setItem("codespace.activeTeamId", "t-1");

    const { currentPath } = await renderRoute("/teams/platform-team/projects");

    await waitFor(() => expect(screen.getByRole("dialog", { name: "Create workspace" })).toBeTruthy());

    fireEvent.change(screen.getByRole("textbox"), { target: { value: "  Design Guild  " } });
    fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(posts).toHaveLength(1));

    // Trimmed — the name is what a person typed, not what they meant to type around.
    expect(JSON.parse(posts[0].body)).toEqual({ name: "Design Guild" });

    await waitFor(() => expect(currentPath()).toBe("/teams/design-guild/projects"));
  });
});
