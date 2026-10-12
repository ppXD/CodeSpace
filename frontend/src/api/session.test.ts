import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { adoptSession, adoptSessionFrom, applySessionHeaders, clearAuthState, cookieSessionLive, CSRF_HEADER, isAuthenticated, migrateLegacySession, signOut } from "./session";

/**
 * Where this browser's session lives. The session is an HttpOnly cookie the page never sees; localStorage holds only a
 * non-secret "on the cookie" flag — and the pre-cookie bearer token, for exactly as long as the API has not confirmed a
 * cookie session, so an API pod that predates the cookie never signs anyone out.
 *
 * The API is a fetch stub keyed by method + path, answering as either side of a rolling deploy would: `cookieApi`
 * confirms the cookie, `preCookieApi` has no /api/auth/session at all (its fallback answers 404).
 */
describe("session", () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let tokenWrites: string[];

  beforeEach(() => {
    tokenWrites = [];
    const setItem = localStorage.setItem.bind(localStorage);
    vi.spyOn(localStorage, "setItem").mockImplementation((key: string, value: string) => {
      if (key === "codespace.jwt") tokenWrites.push(value);
      setItem(key, value);
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  type Answer = (init: RequestInit) => Response;

  function stubApi(answers: Record<string, Answer>) {
    fetchMock = vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = typeof input === "string" ? input : input.toString();
      const key = `${init.method ?? "GET"} ${new URL(url, "http://spa.test").pathname}`;
      return answers[key]?.(init) ?? new Response(JSON.stringify({ code: "not_found" }), { status: 404 });
    });
    vi.stubGlobal("fetch", fetchMock);
  }

  const noContent = () => new Response(null, { status: 204 });
  const unauthorized = () => new Response("", { status: 401 });

  /** An API with the session cookie: the exchange sets it, and the probe answers 204 when the request is signed in. */
  const cookieApi = { "POST /api/auth/session": noContent, "GET /api/auth/session": noContent, "DELETE /api/auth/session": noContent };

  /** An API pod from before the cookie: no such route, so its fallback answers 404 to both. */
  const preCookieApi = {};

  function callsTo(method: string, path: string): RequestInit[] {
    return fetchMock.mock.calls
      .filter(([input, init]) => (init?.method ?? "GET") === method && new URL(String(input), "http://spa.test").pathname === path)
      .map(([, init]) => init as RequestInit);
  }

  describe("adoptSession", () => {
    it("lands on the cookie and never writes the token to localStorage when the API confirms the cookie", async () => {
      stubApi(cookieApi);

      await adoptSession("fresh-token");

      expect(tokenWrites).toEqual([]);
      expect(localStorage.getItem("codespace.jwt")).toBeNull();
      expect(isAuthenticated()).toBe(true);
    });

    it("confirms by presenting the token once, then proving the browser sends the cookie back without it", async () => {
      stubApi(cookieApi);

      await adoptSession("fresh-token");

      const [exchange] = callsTo("POST", "/api/auth/session");
      expect(new Headers(exchange.headers).get("Authorization")).toBe("Bearer fresh-token");
      expect(exchange.credentials).toBe("include");

      const [probe] = callsTo("GET", "/api/auth/session");
      expect(new Headers(probe.headers).has("Authorization")).toBe(false);
      expect(probe.credentials).toBe("include");
    });

    it("keeps the bearer token when the API predates the cookie, so a rolling deploy signs nobody out", async () => {
      stubApi(preCookieApi);

      await adoptSession("fresh-token");

      expect(localStorage.getItem("codespace.jwt")).toBe("fresh-token");
      expect(isAuthenticated()).toBe(true);

      const headers = new Headers();
      applySessionHeaders(headers);
      expect(headers.get("Authorization")).toBe("Bearer fresh-token");
    });

    it("keeps the bearer token when the cookie was set but the browser does not send it back", async () => {
      // An API on another site: SameSite=Strict withholds the cookie, so the probe arrives with nothing.
      stubApi({ "POST /api/auth/session": noContent, "GET /api/auth/session": unauthorized });

      await adoptSession("fresh-token");

      expect(localStorage.getItem("codespace.jwt")).toBe("fresh-token");
    });

    it("keeps the bearer token when the API cannot be reached", async () => {
      vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));

      await adoptSession("fresh-token");

      expect(localStorage.getItem("codespace.jwt")).toBe("fresh-token");
    });

    it("lands on the cookie without asking when the API sent no token, because it withholds one only from a request the cookie carried", async () => {
      // Nothing to exchange, nothing to fall back to — and a probe that met a pre-cookie pod mid-deploy would answer 401
      // for a session that is fine.
      stubApi(preCookieApi);
      localStorage.setItem("codespace.jwt", "stale-pre-cookie-token");
      tokenWrites = [];   // the setup above is not the code under test

      await adoptSession(undefined);

      expect(fetchMock).not.toHaveBeenCalled();
      expect(localStorage.getItem("codespace.jwt")).toBeNull();
      expect(localStorage.getItem("codespace.session")).toBe("cookie");
      expect(tokenWrites).toEqual([]);
    });
  });

  describe("adoptSessionFrom", () => {
    it("settles the session and hands back the response without its token, so no state the page keeps ever holds it", async () => {
      stubApi(cookieApi);
      const response = { token: "fresh-token", expiresAt: "2026-10-13T09:00:00Z", user: { id: "u-1" } };

      const settled = await adoptSessionFrom(response);

      expect(settled).toEqual({ expiresAt: "2026-10-13T09:00:00Z", user: { id: "u-1" } });
      expect("token" in settled).toBe(false);
      expect(isAuthenticated()).toBe(true);
      expect(tokenWrites).toEqual([]);
    });

    it("keeps the token out of what it returns even when it has to fall back to the bearer", async () => {
      stubApi(preCookieApi);

      const settled = await adoptSessionFrom({ token: "fresh-token", user: { id: "u-1" } });

      expect(JSON.stringify(settled)).not.toContain("fresh-token");
      expect(localStorage.getItem("codespace.jwt")).toBe("fresh-token");
    });
  });

  describe("cookieSessionLive", () => {
    it("asks the API with the cookie alone and believes a 204", async () => {
      localStorage.setItem("codespace.session", "cookie");
      stubApi(cookieApi);

      expect(await cookieSessionLive()).toBe(true);

      const [probe] = callsTo("GET", "/api/auth/session");
      expect(probe.credentials).toBe("include");
      expect(new Headers(probe.headers).has("Authorization")).toBe(false);
    });

    it("is not live when the API refuses the cookie or cannot be reached", async () => {
      localStorage.setItem("codespace.session", "cookie");

      stubApi({ "GET /api/auth/session": unauthorized });
      expect(await cookieSessionLive()).toBe(false);

      vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
      expect(await cookieSessionLive()).toBe(false);
    });

    it("does not ask for a browser that is not on the cookie", async () => {
      localStorage.setItem("codespace.jwt", "bearer-token");
      stubApi(cookieApi);

      expect(await cookieSessionLive()).toBe(false);
      expect(fetchMock).not.toHaveBeenCalled();
    });
  });

  describe("migrateLegacySession", () => {
    it("exchanges a pre-cookie token for the cookie once, then deletes it", async () => {
      localStorage.setItem("codespace.jwt", "pre-deploy-token");
      stubApi(cookieApi);

      await migrateLegacySession();

      expect(new Headers(callsTo("POST", "/api/auth/session")[0].headers).get("Authorization")).toBe("Bearer pre-deploy-token");
      expect(localStorage.getItem("codespace.jwt")).toBeNull();
      expect(isAuthenticated()).toBe(true);

      await migrateLegacySession();

      expect(callsTo("POST", "/api/auth/session")).toHaveLength(1);
    });

    it("leaves a pre-cookie token working as the bearer while the API cannot take it yet", async () => {
      localStorage.setItem("codespace.jwt", "pre-deploy-token");
      stubApi(preCookieApi);

      await migrateLegacySession();

      expect(localStorage.getItem("codespace.jwt")).toBe("pre-deploy-token");
      expect(isAuthenticated()).toBe(true);
    });

    it("does nothing for a browser with no pre-cookie token", async () => {
      stubApi(cookieApi);

      await migrateLegacySession();

      expect(fetchMock).not.toHaveBeenCalled();
    });
  });

  describe("applySessionHeaders", () => {
    it("always sends the CSRF header and no Authorization once the browser is on the cookie", () => {
      const headers = new Headers();

      applySessionHeaders(headers);

      expect(headers.get(CSRF_HEADER)).toBe("1");
      expect(headers.has("Authorization")).toBe(false);
    });

    it("can withhold the pre-cookie bearer from a request that must go anonymous", () => {
      localStorage.setItem("codespace.jwt", "stale");
      const headers = new Headers();

      applySessionHeaders(headers, { bearer: false });

      expect(headers.has("Authorization")).toBe(false);
      expect(headers.get(CSRF_HEADER)).toBe("1");
    });
  });

  describe("signOut", () => {
    it("has the API clear the cookie, then forgets every trace of the session", async () => {
      stubApi(cookieApi);
      localStorage.setItem("codespace.session", "cookie");
      localStorage.setItem("codespace.activeTeamId", "team-1");

      await signOut();

      const [cleared] = callsTo("DELETE", "/api/auth/session");
      expect(cleared.credentials).toBe("include");
      expect(new Headers(cleared.headers).get(CSRF_HEADER)).toBe("1");
      expect(isAuthenticated()).toBe(false);
      expect(localStorage.getItem("codespace.activeTeamId")).toBeNull();
    });

    it("signs this page out even when the API cannot be reached", async () => {
      vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
      localStorage.setItem("codespace.jwt", "token");

      await signOut();

      expect(isAuthenticated()).toBe(false);
    });
  });

  it("is signed out with neither a confirmed cookie nor a pre-cookie token", () => {
    expect(isAuthenticated()).toBe(false);

    localStorage.setItem("codespace.session", "cookie");
    expect(isAuthenticated()).toBe(true);

    clearAuthState();
    expect(isAuthenticated()).toBe(false);
  });
});
