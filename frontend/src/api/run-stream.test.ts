import { afterEach, describe, expect, it, vi } from "vitest";

import { streamRunRecords } from "./run-stream";

/**
 * The run-ledger tail is a raw fetch, outside request.ts, so it carries the session on its own: the HttpOnly cookie
 * (a credentialed request — the API is another origin in the deployed image), and a pre-cookie bearer token only for a
 * browser that has not moved onto the cookie yet.
 */
describe("streamRunRecords", () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  async function openTail(): Promise<{ init: RequestInit; headers: Headers }> {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));   // no body: the tail stops at once
    vi.stubGlobal("fetch", fetchMock);
    localStorage.setItem("codespace.activeTeamId", "team-1");

    await streamRunRecords("run-1", () => 0, () => {}, new AbortController().signal);

    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    return { init, headers: new Headers(init.headers) };
  }

  it("tails over the session cookie, with no Authorization header once the browser is on it", async () => {
    localStorage.setItem("codespace.session", "cookie");

    const { init, headers } = await openTail();

    expect(init.credentials).toBe("include");
    expect(headers.has("Authorization")).toBe(false);
    expect(headers.get("X-Team-Id")).toBe("team-1");
  });

  it("still sends a pre-cookie token as the bearer until the browser has moved onto the cookie", async () => {
    localStorage.setItem("codespace.jwt", "pre-deploy-token");

    const { headers } = await openTail();

    expect(headers.get("Authorization")).toBe("Bearer pre-deploy-token");
  });
});
