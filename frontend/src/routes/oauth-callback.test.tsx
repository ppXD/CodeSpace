import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createMemoryHistory, createRouter } from "@tanstack/react-router";
import { render, screen, waitFor } from "@testing-library/react";
import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { routeTree } from "@/routeTree.gen";

/**
 * The page the OAuth popup lands on. Most connects arrive already finished — the API's callback found the binding
 * cookie of the browser that started them. But a browser keeps no cookie from an API on another site, so there the
 * callback hands the code and state to this page instead, in the fragment, and this page finishes the connect as the
 * signed-in user. The API accepts that only from the person who started the connect; what is pinned here is that the
 * page spends the single-use code exactly once, keeps it out of the address bar, and tells its opener how it ended —
 * or, when no page on this origin opened it, stays open so the person can read how it ended.
 */
describe("/oauth-callback", () => {
  /** The SPA page that opened the popup, on this origin — the one window that hears how the connect ended. */
  const opener = { closed: false, postMessage: vi.fn(), location: { get origin() { return window.location.origin; } } };

  beforeEach(() => {
    openedBy(opener);
    vi.spyOn(window, "close").mockImplementation(() => {});
  });

  afterEach(() => {
    opener.postMessage.mockReset();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.clear();
    window.history.replaceState(null, "", "/");
  });

  it("finishes a handed-off connect once, as the signed-in user, and tells the opener once", async () => {
    const fetchMock = stubComplete([200, { credentialId: "cred-42" }]);

    await openAt("/oauth-callback#oauthCode=the%2Fcode&oauthState=the-state");

    await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith({ type: "codespace:oauth-result", status: "success", credentialId: "cred-42" }, window.location.origin));
    // StrictMode runs the settling effect twice. A second run finds the fragment already cleared and, unguarded, settles
    // as a failure first — the opener acts on the first message it hears and reports a connect that succeeded as failed.
    expect(opener.postMessage).toHaveBeenCalledTimes(1);
    expect(completeCalls(fetchMock)).toEqual([{ code: "the/code", state: "the-state" }]);
    expect(screen.getByText("Connected")).toBeTruthy();
    await waitFor(() => expect(window.close).toHaveBeenCalled());
  });

  it("clears the code from the address bar before anything else can read it", async () => {
    stubComplete([200, { credentialId: "cred-42" }]);

    await openAt("/oauth-callback#oauthCode=the-code&oauthState=the-state");

    await waitFor(() => expect(opener.postMessage).toHaveBeenCalled());
    expect(window.location.hash).toBe("");
    expect(window.location.href).not.toContain("the-code");
  });

  it("reports a refusal to the opener — someone other than the starter finishes nothing", async () => {
    stubComplete([400, { code: "oauth_callback_invalid", message: "state token missing, expired, already consumed, or not started by you" }]);

    await openAt("/oauth-callback#oauthCode=c&oauthState=s");

    await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith({ type: "codespace:oauth-result", status: "error", error: "oauth_callback_invalid", description: "state token missing, expired, already consumed, or not started by you" }, window.location.origin));
    expect(screen.getByText("Authorization failed")).toBeTruthy();
  });

  it("asks a signed-out browser to sign in rather than sending it away", async () => {
    stubComplete([401, { code: "unauthorized", message: "Authentication required." }]);

    await openAt("/oauth-callback#oauthCode=c&oauthState=s");

    await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith(expect.objectContaining({ status: "error", error: "unauthorized", description: expect.stringMatching(/sign in/i) }), window.location.origin));
    expect(window.location.pathname).toBe("/oauth-callback");
  });

  it.each([
    ["no page opened it", null],
    ["the page that opened it is on another origin", crossOriginOpener()],
  ])("stays open with how it ended when %s — nobody else would ever say", async (_, popupOpener) => {
    // A connect started on another host than the one it came back to (an alias the API does not list) lands here on
    // an origin its opener is not on: a message to that opener is dropped, and closing would hide the only account of
    // the outcome — "sign in" here, or a credential that was in fact created.
    openedBy(popupOpener);
    stubComplete([401, { code: "unauthorized", message: "Authentication required." }]);

    await openAt("/oauth-callback#oauthCode=c&oauthState=s");

    await waitFor(() => expect(screen.getByText(/sign in to codespace in this browser/i)).toBeTruthy());
    await new Promise((resolve) => setTimeout(resolve, 400));
    expect(window.close).not.toHaveBeenCalled();
  });

  it("asks again when an API pod from before this page answers that it has no such endpoint", async () => {
    // Mid rolling deploy the POST can reach a pod that predates it: a 404 that consumed nothing.
    const fetchMock = stubComplete([404, { code: "not_found", message: "This server has no /api/credentials/oauth/complete." }], [200, { credentialId: "cred-7" }]);

    await openAt("/oauth-callback#oauthCode=c&oauthState=s");

    await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith(expect.objectContaining({ status: "success", credentialId: "cred-7" }), window.location.origin));
    expect(completeCalls(fetchMock)).toHaveLength(2);
  });

  it.each([
    ["?oauthCredentialId=cred-9", { status: "success", credentialId: "cred-9" }],
    ["?oauthError=access_denied&oauthErrorDescription=The%20user%20denied", { status: "error", error: "access_denied", description: "The user denied" }],
  ])("passes on a connect the API already settled (%s) without calling it", async (search, outcome) => {
    const fetchMock = stubComplete();

    await openAt(`/oauth-callback${search}`);

    await waitFor(() => expect(opener.postMessage).toHaveBeenCalledWith({ type: "codespace:oauth-result", ...outcome }, window.location.origin));
    expect(completeCalls(fetchMock)).toHaveLength(0);
  });

  /**
   * Answers POST /api/credentials/oauth/complete with each [status, body] in turn; every other URL is a 404. Each answer
   * arrives a macrotask later, as a network answer does: an answer in the same tick lets React batch a doubled effect's
   * outcome away, and a test of "spends the code once" could then never fail.
   */
  function stubComplete(...answers: [number, unknown][]) {
    const queue = [...answers];
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const [status, body] = url.includes("/api/credentials/oauth/complete") && queue.length > 0 ? queue.shift()! : [404, undefined];

      await new Promise((resolve) => setTimeout(resolve, 20));

      return new Response(body === undefined ? "" : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    });

    vi.stubGlobal("fetch", fetchMock);

    return fetchMock;
  }

  function completeCalls(fetchMock: ReturnType<typeof stubComplete>) {
    return fetchMock.mock.calls
      .filter(([input]) => String(input).includes("/api/credentials/oauth/complete"))
      .map(([, init]) => JSON.parse((init as RequestInit).body as string));
  }

  function openedBy(popupOpener: unknown) {
    Object.defineProperty(window, "opener", { value: popupOpener, configurable: true });
  }

  /** A window on another origin: a browser lets this page see that it exists, and refuses to tell it where it is. */
  function crossOriginOpener() {
    return { closed: false, postMessage: vi.fn(), location: { get origin(): string { throw new DOMException("Blocked a frame from accessing a cross-origin frame.", "SecurityError"); } } };
  }

  /** The popup as a browser opens it — the real address bar, the real route tree, and StrictMode's doubled effects. */
  async function openAt(url: string) {
    localStorage.setItem("codespace.session", "cookie");
    window.history.replaceState(null, "", url);

    const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: [url] }) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <StrictMode>
        <QueryClientProvider client={queryClient}>
          {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
          <RouterProvider router={router as any} />
        </QueryClientProvider>
      </StrictMode>,
    );

    await waitFor(() => expect(router.state.status).toBe("idle"), { timeout: 15_000 });
  }
});
