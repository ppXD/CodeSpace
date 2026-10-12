import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { fetchJson } from "./request";
import { isAuthenticated, migrateLegacySession } from "./session";

/**
 * A rolling deploy, as the SPA meets it: each request lands on whichever API pod the load balancer picks, some from
 * before the session cookie and some from after it. A pre-cookie pod reads only the Authorization header and has no
 * /api/auth/session; a cookie pod reads the cookie too. The browser's cookie jar is modelled here because the page
 * cannot see the real one.
 */
describe("a rolling deploy", () => {
  type Pod = "pre-cookie" | "cookie";

  let jar: string | null;
  let locationAssignMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    jar = null;
    locationAssignMock = vi.fn();
    Object.defineProperty(window, "location", { configurable: true, value: { pathname: "/", assign: locationAssignMock } });
  });

  afterEach(() => { vi.unstubAllGlobals(); });

  /** Routes each request to the next pod in `pods`, in order — the load balancer's choices, scripted. */
  function stubFleet(pods: Pod[]) {
    const queue = [...pods];

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
      const pod = queue.shift() ?? "cookie";
      const path = new URL(String(input), "http://spa.test").pathname;
      const method = init.method ?? "GET";
      const bearer = new Headers(init.headers).get("Authorization")?.replace("Bearer ", "") ?? null;
      const cookie = init.credentials === "include" ? jar : null;

      return pod === "pre-cookie" ? preCookiePod(path, bearer) : cookiePod(method, path, bearer, cookie);
    }));
  }

  function ok(): Response {
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { "Content-Type": "application/json" } });
  }

  function preCookiePod(path: string, bearer: string | null): Response {
    if (bearer == null) return new Response("", { status: 401 });

    return path === "/api/auth/session" ? new Response("", { status: 404 }) : ok();
  }

  function cookiePod(method: string, path: string, bearer: string | null, cookie: string | null): Response {
    const session = bearer ?? cookie;

    if (session == null) return new Response("", { status: 401 });

    if (path !== "/api/auth/session") return ok();

    if (method === "POST") jar = session;

    return new Response(null, { status: 204 });
  }

  it("keeps a browser that moved onto the cookie signed in when a request meets a pre-cookie pod", async () => {
    // Signed in before the deploy: the token is in localStorage. The exchange and its probe meet cookie pods, so the
    // token is deleted; the next request meets a pod that cannot read the cookie, and the probe after it one that can.
    localStorage.setItem("codespace.jwt", "pre-deploy-token");
    stubFleet(["cookie", "cookie", "pre-cookie", "cookie", "cookie"]);

    await migrateLegacySession();

    expect(localStorage.getItem("codespace.jwt")).toBeNull();

    await expect(fetchJson("/api/projects")).rejects.toMatchObject({ status: 401 });

    expect(locationAssignMock).not.toHaveBeenCalled();
    expect(isAuthenticated()).toBe(true);
    await expect(fetchJson("/api/projects")).resolves.toEqual({ ok: true });
  });

  it("signs out when the probe meets a pre-cookie pod too — which is why every API pod rolls before the SPA does", async () => {
    // The limit of what the page can tell: a 401 from both pods looks exactly like a dead session. Rolling the API
    // first means no browser runs this SPA while a pre-cookie pod is still answering.
    localStorage.setItem("codespace.jwt", "pre-deploy-token");
    stubFleet(["cookie", "cookie", "pre-cookie", "pre-cookie"]);

    await migrateLegacySession();
    await expect(fetchJson("/api/projects")).rejects.toMatchObject({ status: 401 });

    expect(locationAssignMock).toHaveBeenCalledWith("/signin");
    expect(isAuthenticated()).toBe(false);
  });

  it("keeps a browser on its bearer token signed in on any pod while the exchange cannot land", async () => {
    localStorage.setItem("codespace.jwt", "pre-deploy-token");
    stubFleet(["pre-cookie", "pre-cookie", "cookie"]);

    await migrateLegacySession();

    expect(localStorage.getItem("codespace.jwt")).toBe("pre-deploy-token");
    await expect(fetchJson("/api/projects")).resolves.toEqual({ ok: true });
    await expect(fetchJson("/api/projects")).resolves.toEqual({ ok: true });
    expect(locationAssignMock).not.toHaveBeenCalled();
  });
});
