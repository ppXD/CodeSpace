import { afterEach, describe, expect, it, vi } from "vitest";

import { oauthApi } from "./oauth";

/**
 * Starting an OAuth connect sets an HttpOnly nonce cookie that the callback later requires — the connect only
 * completes in the browser that started it. When the API sits on another origin, a fetch keeps a response's cookie
 * only if it was sent with credentials, so the start of a connect must always ask for them.
 */
describe("oauthApi.initOAuth", () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  it("asks the browser to keep the nonce cookie the response sets, even across origins", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ authorizeUrl: "https://git.example/authorize", state: "s" }), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);

    await oauthApi.initOAuth({ providerInstanceId: "instance", displayName: "connect" });

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.credentials).toBe("include");
    expect(init.method).toBe("POST");
  });
});

/**
 * A connect whose callback could not tell which browser started it is handed to the SPA's completion page, which
 * finishes it as the signed-in user. The code is single-use and travels in the body, never in a URL.
 */
describe("oauthApi.completeOAuth", () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  it("posts the handed-off code and state with the session", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ credentialId: "cred-1" }), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await oauthApi.completeOAuth({ code: "the-code", state: "the-state" });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toMatch(/\/api\/credentials\/oauth\/complete$/);
    expect(init.method).toBe("POST");
    expect(init.credentials).toBe("include");
    expect(JSON.parse(init.body as string)).toEqual({ code: "the-code", state: "the-state" });
    expect(result).toEqual({ credentialId: "cred-1" });
  });
});
