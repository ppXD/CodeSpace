import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam, ProviderKind, RejectedDelivery, RepositoryWebhookAttemptDetail, RepositoryWebhookCoverage, RepositoryWebhookDetail } from "@/api/types";
import { rejectionCopy } from "@/lib/webhookState";
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

  const refusal = (over: Partial<RejectedDelivery> = {}): RejectedDelivery => ({
    id: "d1",
    receivedAt: new Date(Date.now() - 4 * 60_000).toISOString(),
    repositoryId: "r1",
    reason: "signature_invalid",
    detail: "signature did not validate for webhook 6f3a91c4-2d8e-4b17-9a05-c8e1f2b7d340",
    externalEventId: "5f8a1c22-9b0e-4d31-8e77-1a2b3c4d5e6f",
    rawHeadersRedactedJson: '{"X-Gitlab-Event":"Push Hook","X-Gitlab-Token":"[REDACTED]"}',
    verificationResultJson: '{"validated":false,"verifier_class":"GitLabRepositoryProvider"}',
    ...over,
  });

  /** The tone the row wears, read off the DOM rather than off the copy — the colour is half of what the section says. */
  const toneOf = (headline: string) => screen.getByText(headline).closest(".hk-row")?.getAttribute("data-tone");

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

  function renderPanel(hooks: RepositoryWebhookDetail[], { provider = "GitLab" as ProviderKind, permissions = ["repos.manage"], secret = "s3cr3t-from-the-server", refusals = [] as RejectedDelivery[], cap = 50, coverage = { scope: "Repository", ownerPath: null, hook: null } as RepositoryWebhookCoverage } = {}) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", "t1");

    stub({
      "/webhooks/coverage": coverage,
      "/webhooks/w1/secret": { webhookId: "w1", secret },
      "/webhooks": hooks,
      "/rejected-deliveries": { deliveries: refusals, cap },
      "/api/users/me": me(team(permissions)),
    });

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

  it("does not call a hook that has never fired a delivering one", async () => {
    // The quietest way a hook is broken: the provider accepted it and cannot reach us. Registration
    // is something WE did; delivery is something only the provider can demonstrate, so the row must
    // not claim the second on the strength of the first. It used to read "Delivering · no event has
    // arrived yet" -- a badge contradicting its own caption -- which also made the setup steps below
    // it wrong, since they promise the row turns to Delivering once the first event lands.
    renderPanel([hook()]);

    await waitFor(() => expect(screen.getByText("Ready")).toBeTruthy());

    expect(screen.getByText(/waiting for the first event/)).toBeTruthy();
    expect(screen.queryByText("Delivering")).toBeNull();
  });

  it("promises Delivering in the setup steps only for a state the row can actually reach", async () => {
    // The steps end with "the row above turns to Delivering once the first event lands here". That
    // sentence was false while a freshly registered row already said Delivering: it described a
    // transition the reader could never observe, on a page whose whole job is telling them whether
    // the hook works. The two have to agree, so this asserts them together.
    renderPanel([hook()]);

    await waitFor(() => expect(screen.getByText("Ready")).toBeTruthy());

    expect(screen.queryByText("Delivering")).toBeNull();
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
    renderPanel([hook({ lastReceivedDate: new Date(Date.now() - 60_000).toISOString() })]);

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

  // ── Deliveries that arrived and were refused ───────────────────────────────────
  //
  // The section's whole reason for existing is that these are not one severity. A signature mismatch
  // is broken, an unsubscribed event is noise, and "nothing was listening" is the system working —
  // so the tests below assert the TONE alongside the words, because a page that presented all three
  // the same way would send an operator chasing a fault that does not exist.

  it("says nothing about refused deliveries when none have been refused", async () => {
    renderPanel([hook()]);

    await waitFor(() => expect(screen.getByText("Ready")).toBeTruthy());

    expect(screen.queryByText("Deliveries that were refused")).toBeNull();
  });

  it("reads a signature mismatch as broken, and points at the secret as the thing to change", async () => {
    renderPanel([hook()], { refusals: [refusal()] });

    await waitFor(() => expect(screen.getByText("The signature did not match")).toBeTruthy());

    expect(toneOf("The signature did not match")).toBe("bad");
    expect(screen.getByText(/The secret held at GitLab is not the one CodeSpace signs against/)).toBeTruthy();
    // The provider's own id for the delivery, so this refusal can be found again on GitLab's screen.
    expect(screen.getByText("Delivery 5f8a1c22-9b0e-4d31-8e77-1a2b3c4d5e6f")).toBeTruthy();
    expect(screen.getByText("4 minutes ago")).toBeTruthy();
    // Never the stored discriminator: "signature_invalid" is an identifier, not news.
    expect(document.body.textContent).not.toContain("signature_invalid");
  });

  it("names a malformed payload as something in front of us, not something either end did", async () => {
    // The only one of the five reasons with no test at all before this, and the one whose cause sits
    // furthest from the reader: a body that is not what the provider's own format promises is almost
    // always something between them rewriting the request.
    renderPanel([hook()], { refusals: [refusal({ reason: "malformed_payload" })] });

    await waitFor(() => expect(screen.getByText(rejectionCopy("malformed_payload", "GitLab").headline)).toBeTruthy());

    expect(toneOf(rejectionCopy("malformed_payload", "GitLab").headline)).toBe("bad");
    expect(document.body.textContent).not.toContain("malformed_payload");
  });

  it("reads an unsubscribed event type as noise rather than as a fault", async () => {
    renderPanel([hook()], { refusals: [refusal({ reason: "event_not_mapped", detail: "normalizer for provider GitLab returned null for this payload" })] });

    await waitFor(() => expect(screen.getByText("An event nothing here acts on")).toBeTruthy());

    expect(toneOf("An event nothing here acts on")).toBe("idle");
    expect(screen.getByText(/Harmless\./)).toBeTruthy();
    expect(screen.getByText(/ignoring it costs nothing/)).toBeTruthy();
  });

  it("reads a delivery nobody was listening for as the system working, not as a failure", async () => {
    // The distinction the section exists for. This one is green on purpose: the delivery was
    // verified, read and understood, and nothing subscribed to it. Colouring it like a fault would
    // send an operator hunting something that is not broken.
    renderPanel([hook()], { refusals: [refusal({ reason: "no_matching_activation", detail: "event PushReceivedEvent had no matching enabled activation", verificationResultJson: null })] });

    await waitFor(() => expect(screen.getByText("Nothing was listening for it")).toBeTruthy());

    expect(toneOf("Nothing was listening for it")).toBe("good");
    expect(screen.getByText(/Not a fault\./)).toBeTruthy();
    expect(screen.getByText(/add an activation for this event to that workflow/)).toBeTruthy();
  });

  it("shows the redacted headers and the verifier's diagnostic when a refusal is expanded", async () => {
    renderPanel([hook()], { refusals: [refusal()] });

    await waitFor(() => expect(screen.getByText("The signature did not match")).toBeTruthy());
    fireEvent.click(screen.getAllByRole("button", { name: "Details" }).at(-1)!);

    expect(screen.getByText(/X-Gitlab-Token: \[REDACTED\]/)).toBeTruthy();
    expect(screen.getByText(/X-Gitlab-Event: Push Hook/)).toBeTruthy();
    expect(screen.getByText(/verifier_class/)).toBeTruthy();
    expect(screen.getByText(/signature did not validate for webhook/)).toBeTruthy();
  });

  it("says so when there is no verifier diagnostic rather than dropping the section", async () => {
    // An absent block reads as something withheld. "No diagnostic" is itself the answer: one is
    // only written when a signature fails, so its absence says the refusal was for something else.
    renderPanel([hook()], { refusals: [refusal({ reason: "webhook_inactive", verificationResultJson: null, rawHeadersRedactedJson: null })] });

    await waitFor(() => expect(screen.getByText("The hook is switched off here")).toBeTruthy());
    fireEvent.click(screen.getAllByRole("button", { name: "Details" }).at(-1)!);

    expect(screen.getByText(/No diagnostic — one is written only when a signature fails/)).toBeTruthy();
    expect(screen.getByText("No headers were kept for this delivery.")).toBeTruthy();
  });

  it("keeps a refusal it could not place, and says on the row that it could not place it", async () => {
    // Hiding it would drop the evidence exactly when ingestion is failing earliest — the moment the
    // operator most needs to know that deliveries are arriving and being thrown away.
    renderPanel([hook()], { refusals: [refusal({ repositoryId: null })] });

    await waitFor(() => expect(screen.getByText("The signature did not match")).toBeTruthy());

    expect(screen.getByText(/CodeSpace could not tell which repository this was for/)).toBeTruthy();
  });

  it("says a full list is only the newest, not the whole count", async () => {
    // An unreachable instance retries on a ladder and writes thousands of these in an afternoon, and
    // a list that silently stopped at fifty would read as "fifty happened".
    const many = Array.from({ length: 50 }, (_, i) => refusal({ id: `d${i}`, externalEventId: `delivery-${i}` }));

    renderPanel([hook()], { refusals: many, cap: 50 });

    await waitFor(() => expect(screen.getByText("Deliveries that were refused")).toBeTruthy());

    // Not "there are older ones": a full page can also be exactly the whole of it, and the read
    // cannot tell the two apart. Saying what the list DOES is true either way.
    expect(screen.getByText("Newest 50 — the list stops there")).toBeTruthy();
  });

  it("counts the refusals plainly when the list is short of the cap", async () => {
    renderPanel([hook()], { refusals: [refusal(), refusal({ id: "d2", reason: "event_not_mapped" })], cap: 50 });

    await waitFor(() => expect(screen.getByText("Deliveries that were refused")).toBeTruthy());

    expect(screen.getByText("2 refusals")).toBeTruthy();
  });

  it("says a reason it has no wording for is a reason it has no wording for", async () => {
    // A server ahead of this build. Inventing a diagnosis would be worse than admitting there isn't one.
    renderPanel([hook()], { refusals: [refusal({ reason: "throttled", detail: "too many deliveries in the window" })] });

    await waitFor(() => expect(screen.getByText("Refused on arrival")).toBeTruthy());

    expect(screen.getByText(/does not have wording for/)).toBeTruthy();
    expect(document.body.textContent).not.toContain("throttled:");
  });

  it("names the connection hook that covers a repository with none of its own", async () => {
    // Under connection-wide scope the repository's own list is empty and everything is fine. A tab
    // that rendered "This repository has no webhook" there would be telling the operator to go and
    // fix a working connection — the exact blankness the tab exists to end.
    renderPanel([], {
      coverage: {
        scope: "Connection",
        ownerPath: "acme/platform",
        hook: hook({ id: "c1", lastReceivedDate: new Date(Date.now() - 2 * 60_000).toISOString() }),
      },
    });

    await waitFor(() => expect(screen.getByText("acme/platform")).toBeTruthy());

    expect(screen.queryByText("This repository has no webhook")).toBeNull();
    expect(screen.getByText("Delivering")).toBeTruthy();
    expect(screen.getByText(/One hook on acme\/platform at GitLab covers it/)).toBeTruthy();
  });

  it("gives the covering hook the same diagnosis a repository hook would get", async () => {
    // Same lifecycle, same question, so the same words — and the attempt timeline has to come with
    // it, or the operator can see that nothing is arriving and nothing about why.
    renderPanel([], {
      coverage: {
        scope: "Connection",
        ownerPath: "acme",
        hook: hook({ id: "c1", registrationStatus: "DeadLettered", attempts: 10, attemptTimeline: [attempt(1, 403)] }),
      },
    });

    await waitFor(() => expect(screen.getByText("Not delivering")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Why, and how to fix it" }));

    expect(screen.getByText(/refused to create the hook/)).toBeTruthy();
    expect(screen.getByText("All 1 attempt answered 403.")).toBeTruthy();
  });

  it("says so when connection-wide scope has nothing covering the repository", async () => {
    // The state that is neither "has a hook" nor "per-repository". Silence here is the blank tab again.
    renderPanel([], { coverage: { scope: "Connection", ownerPath: null, hook: null } });

    await waitFor(() => expect(screen.getByText("No hook covers this repository")).toBeTruthy());

    expect(screen.getByText(/Nothing will arrive until one is registered/)).toBeTruthy();
  });

  it("reads a delivery for an unbound repository as expected traffic, not a fault", async () => {
    // A group hook carries every project under the owner. Rendering that in the same alarmed tone as
    // a signature mismatch would send an operator hunting a fault that does not exist.
    renderPanel([hook()], { refusals: [refusal({ reason: "repository_not_bound", detail: "connection webhook 7f3a delivered an event for acme/someone-elses-project, which is not bound in CodeSpace" })] });

    await waitFor(() => expect(screen.getByText("For a repository nothing here has bound")).toBeTruthy());

    expect(toneOf("For a repository nothing here has bound")).toBe("idle");
    // The cap is said out loud: one row a day standing for many must not be counted as one delivery.
    expect(screen.getByText(/At most one of these is recorded per repository per day/)).toBeTruthy();
  });

  it("tells a retired hook apart from one an operator switched off", async () => {
    renderPanel([hook()], { refusals: [refusal({ reason: "webhook_retired", detail: "webhook 7f3a was retired (Cancelled) and no longer accepts deliveries" })] });

    await waitFor(() => expect(screen.getByText("The hook was retired and is still sending")).toBeTruthy());

    expect(toneOf("The hook was retired and is still sending")).toBe("bad");
    expect(screen.getByText(/Remove the hook by hand at GitLab/)).toBeTruthy();
  });
});
