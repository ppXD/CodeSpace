import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/oauth";

import { initAtFirstAcceptedReturnUrl, oauthReturnUrlsFor } from "./use-oauth-flow";

/**
 * Where a connect brings its popup back to. A connect whose callback cannot find the browser's binding cookie — started
 * on another host than the callback names — is handed to the completion page on the origin it named, and only the
 * page's own origin holds the session and the opener that finish it. Vite prints http://127.0.0.1:5180 while
 * OAuth:CallbackUrl names localhost:5180, so a path there brought the popup back to localhost: signed out, its message
 * to the 127.0.0.1 opener dropped, the connect reported as cancelled. A path stays the fallback with the API on the
 * page's own origin, for an origin the API does not list; in direct mode it would land on the API's host instead.
 */
describe("oauthReturnUrlsFor", () => {
  it.each([
    ["the dev proxy, on the address Vite prints", "http://127.0.0.1:5180", ""],
    ["the dev proxy, on localhost", "http://localhost:5180", ""],
    ["one host fronting both", "https://codespace.example", "https://codespace.example"],
    ["the API named with a path on this origin", "https://codespace.example", "https://codespace.example/api-root"],
  ])("asks for the page's own origin, then a path, when the API answers on the page's origin — %s", (_, pageOrigin, apiBase) => {
    expect(oauthReturnUrlsFor(pageOrigin, apiBase)).toEqual([`${pageOrigin}/oauth-callback`, "/oauth-callback"]);
  });

  it.each([
    ["another port on the same host", "http://localhost:5180", "http://localhost:5099"],
    ["another site", "https://spa.example", "https://api.example/v1"],
  ])("asks only for the page's own origin when the API answers elsewhere — %s", (_, pageOrigin, apiBase) => {
    expect(oauthReturnUrlsFor(pageOrigin, apiBase)).toEqual([`${pageOrigin}/oauth-callback`]);
  });
});

/**
 * Starting a connect at the first return address the API accepts. Both API builds refuse a return address they do not
 * accept with 400 invalid_request before starting anything, so asking again with the next address leaves nothing behind;
 * any other answer is the outcome, since the return address is the only thing that differs between tries.
 */
describe("initAtFirstAcceptedReturnUrl", () => {
  const request = { providerInstanceId: "pi-1", displayName: "Maya's GitLab" };
  const started = { authorizeUrl: "https://gitlab.example/oauth/authorize?state=s", state: "s" };

  afterEach(() => { vi.unstubAllGlobals(); });

  it("starts at the page's own origin when the API accepts it", async () => {
    const fetchMock = stubInit([200, started]);

    await expect(initAtFirstAcceptedReturnUrl(request, ["http://127.0.0.1:5180/oauth-callback", "/oauth-callback"])).resolves.toEqual(started);

    expect(returnUrlsAsked(fetchMock)).toEqual(["http://127.0.0.1:5180/oauth-callback"]);
  });

  it.each([
    ["this build", "returnUrl must be a path on this site, or an address on an origin this deployment serves its web app from — App:PublicBaseUrl, or one listed in Cors:AllowedOrigins. (Parameter 'returnUrl')"],
    ["a build from before it read Cors:AllowedOrigins", "returnUrl must be a path on this site or an address on its public origin. (Parameter 'returnUrl')"],
  ])("falls back to a path when the API does not list the page's origin — %s", async (_, message) => {
    const fetchMock = stubInit([400, { code: "invalid_request", message }], [200, started]);

    await expect(initAtFirstAcceptedReturnUrl(request, ["https://alias.example/oauth-callback", "/oauth-callback"])).resolves.toEqual(started);

    expect(returnUrlsAsked(fetchMock)).toEqual(["https://alias.example/oauth-callback", "/oauth-callback"]);
  });

  it.each([
    ["a provider that is not OAuth-configured", 400, "oauth_callback_invalid"],
    ["a caller without the permission", 403, "forbidden"],
  ])("does not ask again for any other refusal — %s", async (_, status, code) => {
    const fetchMock = stubInit([status, { code, message: "refused" }]);

    await expect(initAtFirstAcceptedReturnUrl(request, ["https://spa.example/oauth-callback", "/oauth-callback"])).rejects.toMatchObject({ status, code });

    expect(returnUrlsAsked(fetchMock)).toHaveLength(1);
  });

  it("reports the last refusal when no address is accepted", async () => {
    stubInit([400, { code: "invalid_request", message: "first" }], [400, { code: "invalid_request", message: "last" }]);

    const refused = await initAtFirstAcceptedReturnUrl(request, ["https://alias.example/oauth-callback", "/oauth-callback"]).catch((err: unknown) => err);

    expect(refused).toBeInstanceOf(ApiError);
    expect((refused as ApiError).message).toBe("last");
  });

  /** Answers POST /api/credentials/oauth/init with each [status, body] in turn. */
  function stubInit(...answers: [number, unknown][]) {
    const queue = [...answers];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => {
      const [status, body] = queue.shift() ?? [500, { code: "unexpected", message: "no more answers" }];

      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    });

    vi.stubGlobal("fetch", fetchMock);

    return fetchMock;
  }

  function returnUrlsAsked(fetchMock: ReturnType<typeof stubInit>) {
    return fetchMock.mock.calls.map(([input, init]) => {
      expect(String(input)).toMatch(/\/api\/credentials\/oauth\/init$/);

      return JSON.parse((init as RequestInit).body as string).returnUrl;
    });
  }
});
