import { Outlet } from "@tanstack/react-router";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { MeResponse } from "@/api/types";
import { renderRoute } from "@/test/route-harness";

vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

/**
 * Signing in lands the browser on the HttpOnly session cookie, and the token the response also carries (for API
 * clients) is never written where a page script could read it. Against an API that predates the cookie — one side of
 * a rolling deploy — the same sign-in falls back to the bearer token, because the alternative is signing the person
 * straight back out.
 */
describe("sign-in", () => {
  const user: MeResponse = { id: "u-1", email: "maya@team.dev", name: "Maya", passwordMustChange: false, permissions: [], teams: [] };

  let tokenWrites: string[];
  let requests: Request[];

  beforeEach(() => {
    tokenWrites = [];
    requests = [];
    const setItem = localStorage.setItem.bind(localStorage);
    vi.spyOn(localStorage, "setItem").mockImplementation((key: string, value: string) => {
      if (key === "codespace.jwt") tokenWrites.push(value);
      setItem(key, value);
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  /** A method-aware API stub: `cookie` answers /api/auth/session as an API with the session cookie does, `false` as one from before it (404). */
  function stubApi(options: { cookie: boolean }) {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = new Request(input instanceof Request ? input : new URL(String(input), "http://spa.test"), init);
      requests.push(request);

      const route = `${request.method} ${new URL(request.url).pathname}`;
      if (route === "POST /api/auth/sign-in") return json({ token: "minted-token", expiresAt: "2026-10-13T09:00:00Z", user });
      if (route === "GET /api/users/me") return json(user);
      if (route.endsWith(" /api/auth/session") && options.cookie) return new Response(null, { status: 204 });

      return json({ code: "not_found" }, 404);
    }));
  }

  function json(body: unknown, status = 200): Response {
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  }

  async function signIn() {
    const view = await renderRoute("/signin");
    localStorage.clear();   // the harness marks a session; this visitor has none

    fireEvent.change(screen.getByLabelText("Email or name"), { target: { value: "maya@team.dev" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "correct-horse-battery" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(view.currentPath()).toBe("/"));

    return view;
  }

  it("lands on the session cookie and never writes the token to localStorage", async () => {
    stubApi({ cookie: true });

    await signIn();

    expect(tokenWrites).toEqual([]);
    expect(localStorage.getItem("codespace.jwt")).toBeNull();
    expect(localStorage.getItem("codespace.session")).toBe("cookie");

    const signInRequest = requests.find((r) => r.url.endsWith("/api/auth/sign-in"))!;
    expect(signInRequest.credentials).toBe("include");   // or a cross-origin API's Set-Cookie is dropped
  });

  it("stays signed in on the bearer token when the API predates the cookie", async () => {
    stubApi({ cookie: false });

    await signIn();

    expect(localStorage.getItem("codespace.jwt")).toBe("minted-token");
    expect(localStorage.getItem("codespace.session")).toBeNull();
  });
});
