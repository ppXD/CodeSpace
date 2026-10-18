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
 * A Reconnect names the credential it re-authorizes: the new token goes into that row, so the repositories bound through it
 * keep working. A client that does not name one connects a new credential exactly as before, and an API that predates the
 * field ignores it — so the field is only on the wire when it was given.
 */
describe("oauthApi.initOAuth naming a credential to replace", () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  it("sends replaceCredentialId when a credential is named, and no such field otherwise", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => new Response(JSON.stringify({ authorizeUrl: "https://git.example/authorize", state: "s" }), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);

    await oauthApi.initOAuth({ providerInstanceId: "instance", displayName: "connect", replaceCredentialId: "cred-1" });
    await oauthApi.initOAuth({ providerInstanceId: "instance", displayName: "connect" });

    expect(JSON.parse((fetchMock.mock.calls[0][1] as RequestInit).body as string).replaceCredentialId).toBe("cred-1");
    expect("replaceCredentialId" in JSON.parse((fetchMock.mock.calls[1][1] as RequestInit).body as string)).toBe(false);
  });
});

/**
 * Replacing a token puts it into the credential in place. The token travels in the body of a PUT to the credential's own
 * route and never in a URL.
 */
describe("oauthApi.replaceCredentialToken", () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  it("puts the token in the body of the credential's token route", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ credentialId: "cred 1", providerUsername: "nacho" }), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await oauthApi.replaceCredentialToken("cred 1", "glpat-fresh");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toMatch(/\/api\/credentials\/cred%201\/token$/);
    expect(url).not.toContain("glpat-fresh");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toEqual({ token: "glpat-fresh" });
    expect(result).toEqual({ credentialId: "cred 1", providerUsername: "nacho" });
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
