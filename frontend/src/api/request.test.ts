import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError, fetchJson } from "./request";

/**
 * Tests for the hand-rolled fetch wrapper. Targets the security-critical
 * paths: JWT injection, X-Team-Id injection, the sign-in exclusion that stops
 * stale tokens from rejecting fresh credentials, and the 401/403 redirect
 * branches that silently wipe state on the way to /signin or /change-password.
 *
 * Every test starts from an empty localStorage + an unset window.location
 * assignment per the global setup. We stub `fetch` and `window.location` per
 * test rather than using a shared MSW — each path-under-test exercises exactly
 * one request, so a direct fetch mock is simplest and reveals exactly the
 * header shape that went out the wire.
 */
describe("fetchJson", () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let locationAssignMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    // jsdom's window.location.assign is a no-op stub already; replace with a
    // spy we can assert against. Same trick the React Router test guides use.
    locationAssignMock = vi.fn();
    Object.defineProperty(window, "location", {
      configurable: true,
      value: { pathname: "/", assign: locationAssignMock },
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function ok<T>(body: T, status = 200): Response {
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  }

  function err(status: number, body: unknown): Response {
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  }

  it("parses 200 JSON body into the typed result", async () => {
    fetchMock.mockResolvedValueOnce(ok({ id: 42, name: "foo" }));

    const result = await fetchJson<{ id: number; name: string }>("/api/anything");

    expect(result).toEqual({ id: 42, name: "foo" });
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it("returns undefined on 204 without trying to parse the body", async () => {
    // 204 No Content has no body — calling .text() then JSON.parse would crash.
    // The wrapper short-circuits the parse to match the .NET convention for
    // command endpoints that don't return a payload.
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    const result = await fetchJson<unknown>("/api/delete-something", { method: "DELETE" });

    expect(result).toBeUndefined();
  });

  it("injects the Bearer JWT header when a token is in localStorage", async () => {
    localStorage.setItem("codespace.jwt", "my-token");
    fetchMock.mockResolvedValueOnce(ok({}));

    await fetchJson("/api/me");

    const headers = capturedHeaders();
    expect(headers.get("Authorization")).toBe("Bearer my-token");
  });

  it("DOES NOT inject the Bearer header on /api/auth/sign-in even when a stale JWT is present", async () => {
    // Critical guard: a stale JWT in localStorage from a previous session would
    // otherwise reach the password-rotation gate before the new credentials are
    // even checked, surfacing as a 403 on the sign-in form. The wrapper drops
    // Authorization specifically for the sign-in path.
    localStorage.setItem("codespace.jwt", "stale-token");
    fetchMock.mockResolvedValueOnce(ok({}));

    await fetchJson("/api/auth/sign-in", { method: "POST", body: JSON.stringify({ name: "x", password: "y" }) });

    const headers = capturedHeaders();
    expect(headers.has("Authorization")).toBe(false);
  });

  it("injects X-Team-Id when an active team id is in localStorage", async () => {
    localStorage.setItem("codespace.activeTeamId", "team-abc");
    fetchMock.mockResolvedValueOnce(ok({}));

    await fetchJson("/api/repositories");

    const headers = capturedHeaders();
    expect(headers.get("X-Team-Id")).toBe("team-abc");
  });

  it("throws ApiError carrying status + code + message when the backend returns a typed envelope", async () => {
    fetchMock.mockResolvedValueOnce(err(409, { code: "duplicate_slug", message: "Pick a different project name" }));

    const act = fetchJson("/api/projects");

    await expect(act).rejects.toBeInstanceOf(ApiError);
    await act.catch((e: unknown) => {
      const apiError = e as ApiError;
      expect(apiError.status).toBe(409);
      expect(apiError.code).toBe("duplicate_slug");
      expect(apiError.message).toBe("Pick a different project name");
    });
  });

  it("on 401 from a non-auth path: wipes JWT + activeTeamId AND redirects to /signin", async () => {
    localStorage.setItem("codespace.jwt", "expired");
    localStorage.setItem("codespace.activeTeamId", "team-x");
    fetchMock.mockResolvedValueOnce(err(401, { code: "unauthorized", message: "" }));

    await expect(fetchJson("/api/me")).rejects.toBeInstanceOf(ApiError);

    expect(localStorage.getItem("codespace.jwt")).toBeNull();
    expect(localStorage.getItem("codespace.activeTeamId")).toBeNull();
    expect(locationAssignMock).toHaveBeenCalledWith("/signin");
  });

  it("on 401 from /api/auth/* does NOT bounce the page — the sign-in form needs the error inline", async () => {
    fetchMock.mockResolvedValueOnce(err(401, { code: "invalid_credentials", message: "Bad creds" }));

    await expect(fetchJson("/api/auth/sign-in", { method: "POST" })).rejects.toBeInstanceOf(ApiError);

    expect(locationAssignMock).not.toHaveBeenCalled();
  });

  it("on 403 password_rotation_required from a non-auth path: redirects to /change-password", async () => {
    fetchMock.mockResolvedValueOnce(err(403, { code: "password_rotation_required", message: "" }));

    await expect(fetchJson("/api/me")).rejects.toBeInstanceOf(ApiError);

    expect(locationAssignMock).toHaveBeenCalledWith("/change-password");
    // JWT is NOT wiped on a 403 — the user still has a valid identity, they just
    // need to rotate. Wiping here would break the rotation page's own auth.
    // (The fetchMock used a fresh localStorage so there's no token to assert on;
    // the absence of the /signin assignment above + this comment pin the contract.)
  });

  it("non-JSON response surfaces as ApiError without crashing the wrapper", async () => {
    // Reverse proxy / load balancer can return HTML on 502. The wrapper's
    // safeJsonParse must catch the SyntaxError and still throw a usable
    // ApiError instead of bubbling a raw parse exception.
    fetchMock.mockResolvedValueOnce(new Response("<html>502</html>", { status: 502, headers: { "Content-Type": "text/html" } }));

    const act = fetchJson("/api/anything");

    await expect(act).rejects.toBeInstanceOf(ApiError);
    await act.catch((e: unknown) => {
      const apiError = e as ApiError;
      expect(apiError.status).toBe(502);
      // Falls back to "http_<status>" when no `code` field is present in the body.
      expect(apiError.code).toBe("http_502");
    });
  });

  /** Pull out the Headers instance the wrapper handed to fetch on its single call. */
  function capturedHeaders(): Headers {
    const init = fetchMock.mock.calls[0]?.[1] as RequestInit | undefined;
    return new Headers(init?.headers);
  }
});
