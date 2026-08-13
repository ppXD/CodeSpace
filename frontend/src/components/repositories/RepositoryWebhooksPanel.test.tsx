import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam, ProviderKind, RepositoryWebhookAttemptDetail, RepositoryWebhookDetail } from "@/api/types";
import { RepositoryWebhooksPanel } from "./RepositoryWebhooksPanel";

/**
 * The tab's job is to answer "are events arriving, and if not, why" — so the assertions are about the
 * WORDS, not about the shape of the markup.
 *
 * <p>A registration status is the queue's vocabulary, and rendering it would hand the reader a term
 * they have to already understand to act on. These tests drive the component with real rows of each
 * state and pin the sentence the operator gets instead.</p>
 */
describe("repository webhooks panel", () => {
  const team = (permissions: string[]): MeTeam => ({
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role: permissions.length > 0 ? "Admin" : "Member", permissions,
    memberCount: 3, repositoryCount: 1, projectCount: 1, workflowCount: 0,
  });

  const me = (t: MeTeam): MeResponse => ({ id: "u1", email: "u@test.local", name: "Mars P", teams: [t], permissions: [], passwordMustChange: false });

  const hook = (over: Partial<RepositoryWebhookDetail> = {}): RepositoryWebhookDetail => ({
    id: "w1",
    active: true,
    registrationStatus: "Registered",
    attempts: 0,
    nextAttemptAt: "2026-08-13T14:00:00+00:00",
    lastReceivedDate: null,
    callbackUrl: "https://codespace.test/api/webhooks/6f3a91c4-2d8e-4b17-9a05-c8e1f2b7d340",
    externalId: "4127",
    subscribedEvents: ["Push events", "Merge request events"],
    lastError: null,
    attemptTimeline: [],
    ...over,
  });

  const attempt = (n: number, statusCode: number | null, over: Partial<RepositoryWebhookAttemptDetail> = {}): RepositoryWebhookAttemptDetail => ({
    attemptNumber: n,
    attemptedAt: `2026-08-13T13:${String(30 + n).padStart(2, "0")}:00+00:00`,
    error: statusCode == null ? "The operation was canceled." : `${statusCode} Forbidden`,
    statusCode,
    responseBody: statusCode == null ? null : `{"message":"${statusCode} Forbidden - You are not allowed to manage hooks on this project"}`,
    requestMethod: "POST",
    requestUrl: "https://gitlab.test/api/v4/projects/4471293/hooks",
    requestBody: '{"url":"https://codespace.test/api/webhooks/6f3a91c4","token":"***"}',
    requestHeadersJson: '{"PRIVATE-TOKEN":"***"}',
    ...over,
  });

  /** `find` walks insertion order, so the more specific webhook route has to be declared first. */
  function stub(routes: Record<string, unknown>) {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), {
        status: body === undefined ? 404 : 200,
        headers: { "Content-Type": "application/json" },
      });
    }));
  }

  function renderPanel(hooks: RepositoryWebhookDetail[], { provider = "GitLab" as ProviderKind, permissions = ["repos.manage"], secret = "s3cr3t-from-the-server" } = {}) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", "t1");

    stub({ "/webhooks/w1/secret": { webhookId: "w1", secret }, "/webhooks": hooks, "/api/users/me": me(team(permissions)) });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    return render(
      <QueryClientProvider client={client}>
        <RepositoryWebhooksPanel repositoryId="r1" fullPath="ruhappy/ruhappy-web" provider={provider} />
      </QueryClientProvider>,
    );
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("says a registered hook is delivering, and names the hook it is", async () => {
    renderPanel([hook({ lastReceivedDate: new Date(Date.now() - 3 * 60_000).toISOString() })]);

    await waitFor(() => expect(screen.getByText("Delivering")).toBeTruthy());

    // The provider's own id for the hook, so it can be found on the other screen.
    expect(screen.getByText(/GitLab hook #4127/)).toBeTruthy();
    expect(screen.getByText(/last delivery 3 minutes ago/)).toBeTruthy();
  });

  it("says a registered hook that has never fired has never fired", async () => {
    // The quietest way a hook is broken: the provider accepted it and cannot reach us. A row that
    // said only "Delivering" would be actively wrong.
    renderPanel([hook()]);

    await waitFor(() => expect(screen.getByText("Delivering")).toBeTruthy());

    expect(screen.getByText(/no event has arrived yet/)).toBeTruthy();
  });

  it("says a hook still on the ladder is registering, with which attempt and when the next is", async () => {
    renderPanel([hook({ registrationStatus: "Failed", attempts: 2, nextAttemptAt: new Date(Date.now() + 4 * 60_000).toISOString(), lastError: "403 Forbidden" })]);

    await waitFor(() => expect(screen.getByText("Registering")).toBeTruthy());

    expect(screen.getByText(/Attempt 3 · next try in 4 minutes/)).toBeTruthy();
  });

  it("says a dead-lettered hook is not delivering, and that nothing has arrived at all", async () => {
    // Never "DeadLettered": that is where the job ended up, not the answer to the question asked.
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());

    expect(screen.queryByText(/DeadLettered/)).toBeNull();
    expect(screen.getByText(/Gave up after 10 attempts · nothing has arrived at all/)).toBeTruthy();
  });

  it("names the likely cause of a 403 instead of leaving the reader to infer it", async () => {
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText(/Creating a project hook needs Maintainer/)).toBeTruthy();
    // The request we sent and the answer we got, both verbatim.
    expect(screen.getByText(/POST https:\/\/gitlab\.test\/api\/v4\/projects\/4471293\/hooks/)).toBeTruthy();
    expect(screen.getByText(/PRIVATE-TOKEN: \*\*\*/)).toBeTruthy();
    expect(screen.getByText(/You are not allowed to manage hooks on this project/)).toBeTruthy();
  });

  it("reads a run of identical failures differently from a run that changed", async () => {
    const allForbidden = [1, 2, 3].map((n) => attempt(n, 403));

    const { unmount } = renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 3, attemptTimeline: allForbidden })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText("All 3 attempts answered 403.")).toBeTruthy();

    unmount();

    const timedOutThenForbidden = [attempt(1, null), attempt(2, null), attempt(3, 403)];

    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 3, attemptTimeline: timedOutThenForbidden })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText("2 attempts got no answer at all, then 1 attempt answered 403.")).toBeTruthy();
  });

  it("renders a missing status code as no answer at all, never as a blank", async () => {
    // The absence IS the diagnosis: the request was made and nothing on the other end replied.
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 4, attemptTimeline: [attempt(1, null)] })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText(/The call never got an answer — no HTTP response at all/)).toBeTruthy();
    expect(screen.getByText("No answer at all — the request never reached an HTTP response.")).toBeTruthy();
    // And the timeline column says so too, rather than showing an empty cell.
    expect(screen.getByText("no answer")).toBeTruthy();
  });

  it("lists every attempt, not only the last one", async () => {
    const timeline = [attempt(1, null), attempt(2, 403), attempt(3, 403)];

    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 3, attemptTimeline: timeline })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText("All 3 attempts")).toBeTruthy();
    expect(screen.getByText("#1")).toBeTruthy();
    expect(screen.getByText("#2")).toBeTruthy();
    expect(screen.getByText("#3")).toBeTruthy();
  });

  it("keeps the secret out of the document until it is asked for", async () => {
    // A separate endpoint exists so that opening this tab never puts on the wire the one value that
    // can forge a delivery this repository would accept. That is only true if nothing pre-fetches it.
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    // Settled rather than same-tick: a pre-fetch fired on mount or on expand resolves a microtask later,
    // and an assertion on this tick would pass straight through it — which is what the first version of
    // this test did.
    await new Promise((resolve) => setTimeout(resolve, 50));

    const secretCalls = () => (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.filter(([url]) => String(url).includes("/secret"));

    expect(secretCalls()).toHaveLength(0);
    expect(document.body.textContent).not.toContain("s3cr3t-from-the-server");

    fireEvent.click(screen.getByRole("button", { name: /Reveal/ }));

    await waitFor(() => expect(screen.getByText("s3cr3t-from-the-server")).toBeTruthy());

    // And it can be put away again.
    fireEvent.click(screen.getByRole("button", { name: /Hide/ }));

    await waitFor(() => expect(document.body.textContent).not.toContain("s3cr3t-from-the-server"));
  });

  it("offers no reveal to somebody who may not manage repositories", async () => {
    // Absent rather than present-and-refusing: a Member reads the whole diagnosis, and the secret is
    // the one part of it that is an Admin's to hand over.
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })], { permissions: [] });

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.queryByRole("button", { name: /Reveal/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Retry now/ })).toBeNull();
    expect(screen.getByText("Only an admin can reveal the signing secret.")).toBeTruthy();
  });

  it("uses GitLab's own field labels for a GitLab repository", async () => {
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })], { provider: "GitLab" });

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText("Secret token")).toBeTruthy();
    expect(screen.getByText("URL")).toBeTruthy();
    expect(screen.getByText("Trigger")).toBeTruthy();
    expect(screen.getByText("Merge request events")).toBeTruthy();
    expect(screen.getByText("Add new webhook")).toBeTruthy();

    // GitHub's words must not appear on a GitLab screen — they are what the reader would hunt for.
    expect(screen.queryByText("Payload URL")).toBeNull();
    expect(screen.queryByText("Let me select individual events")).toBeNull();
  });

  it("uses GitHub's own field labels for a GitHub repository", async () => {
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })], { provider: "GitHub" });

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText("Payload URL")).toBeTruthy();
    expect(screen.getByText("Secret")).toBeTruthy();
    expect(screen.getByText("Content type")).toBeTruthy();
    expect(screen.getByText("application/json")).toBeTruthy();
    expect(screen.getByText("Let me select individual events")).toBeTruthy();

    // And the 403 sentence names GitHub's requirement, not GitLab's.
    expect(screen.getByText(/Creating a repository hook needs admin rights/)).toBeTruthy();
    expect(screen.queryByText("Secret token")).toBeNull();
  });

  it("closes the loop: the last step says a test event flips the row on its own", async () => {
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText("Test → Push events")).toBeTruthy();
    expect(screen.getByText(/turns to/)).toBeTruthy();
    expect(screen.getByText(/on its own once the first event lands here/)).toBeTruthy();
  });

  it("offers the callback URL to copy", async () => {
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText("https://codespace.test/api/webhooks/6f3a91c4-2d8e-4b17-9a05-c8e1f2b7d340")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Copy the callback URL" })).toBeTruthy();
  });

  it("re-queues a dead-lettered registration on request", async () => {
    renderPanel([hook({ registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: /Retry now/ }));

    await waitFor(() => expect(
      (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.some(([url, init]) =>
        String(url).includes("/webhooks/w1/retry") && (init as RequestInit | undefined)?.method === "POST"),
    ).toBe(true));
  });

  it("offers no retry on a hook that is already delivering", async () => {
    // Only Failed and DeadLettered can be re-queued; the server answers 400 for anything else, and a
    // button that exists to be refused is not how the rule gets communicated.
    renderPanel([hook()]);

    await waitFor(() => expect(screen.getByText("Delivering")).toBeTruthy());

    expect(screen.queryByRole("button", { name: /Retry now/ })).toBeNull();
  });

  it("says a disabled hook is not delivering even though it is registered", async () => {
    // The provider keeps sending and ingestion keeps rejecting. Reading the registration alone here
    // would be the page's worst possible lie.
    renderPanel([hook({ active: false, lastReceivedDate: new Date().toISOString() })]);

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());

    expect(screen.getByText(/Turned off here/)).toBeTruthy();
  });
});
