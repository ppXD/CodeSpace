import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ProfileAbandonmentSummary, ProfilePlacementOutcome, ProfilePlacementSummary, ProfilePlacementTotal } from "@/api/storage";

import { StoragePlacementDrain } from "./StoragePlacementDrain";

const profileId = "profile-1";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function placement(overrides: Partial<ProfilePlacementSummary> = {}): ProfilePlacementSummary {
  return {
    locationId: "location-1",
    artifactObjectId: "object-1",
    state: "Available",
    objectKey: "artifacts/aaaa",
    profileRevision: 1,
    sizeBytes: 2048,
    verifiedAt: "2026-08-20T10:00:00Z",
    lastErrorCode: null,
    ...overrides,
  };
}

function summary(overrides: Partial<ProfileAbandonmentSummary> = {}): ProfileAbandonmentSummary {
  return {
    examined: 1,
    abandoned: 1,
    stillServed: 0,
    unanswered: 0,
    remaining: 0,
    stoppedBy: null,
    outcomes: [{ locationId: "location-1", objectKey: "artifacts/aaaa", outcome: "Abandoned", detail: "no such key" }],
    ...overrides,
  };
}

/** A pass that reached `count` placements and got nothing usable back from any of them, all for the same reason. */
function unanswered(count: number, detail: string | null): ProfilePlacementOutcome[] {
  return Array.from({ length: count }, (_, index) => ({
    locationId: `location-${index}`,
    objectKey: `artifacts/${index}`,
    outcome: "Unanswered" as const,
    detail,
  }));
}

interface Stubs {
  totals?: ProfilePlacementTotal[];
  placements?: ProfilePlacementSummary[];
  passes?: ProfileAbandonmentSummary[];
}

function renderDrain(stubs: Stubs = {}) {
  const passes = [...(stubs.passes ?? [summary()])];
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", "team-1");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
    if (path === `/api/storage/profiles/${profileId}/placements/totals`) return json(stubs.totals ?? [{ state: "Available", count: 5, sizeBytes: 5120 }]);
    if (path === `/api/storage/profiles/${profileId}/placements`) return json({ items: stubs.placements ?? [], nextCursor: null });
    if (path === `/api/storage/profiles/${profileId}/placements/abandon`) return json(passes.shift() ?? summary());
    return json({ code: "not_found", message: `No stub for ${path}` }, 404);
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });

  return render(<QueryClientProvider client={client}><StoragePlacementDrain profileId={profileId} disabled={false} /></QueryClientProvider>);
}

function drainButton() {
  return screen.getByRole("button", { name: /Abandon/ });
}

/** The control only goes live once the totals say there is a population to reduce, so a click before that is a no-op. */
async function clickAbandon() {
  await waitFor(() => expect(drainButton()).toBeEnabled());
  fireEvent.click(drainButton());
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("StoragePlacementDrain", () => {
  it("shows what the profile still holds, so the retirement refusal has a population behind it", async () => {
    renderDrain({
      totals: [{ state: "Available", count: 3, sizeBytes: 6144 }, { state: "Missing", count: 1, sizeBytes: 1024 }],
      placements: [placement(), placement({ locationId: "location-2", objectKey: "artifacts/bbbb", state: "Missing" })],
    });

    const totals = await screen.findByRole("list", { name: "Placements by state" });
    expect(within(totals).getByText(/Available/)).toHaveTextContent("3");
    expect(within(totals).getByText(/Missing/)).toHaveTextContent("1");

    const list = await screen.findByRole("list", { name: "Placements under this profile" });
    expect(within(list).getByText("artifacts/aaaa")).toBeInTheDocument();
    expect(within(list).getByText("artifacts/bbbb")).toBeInTheDocument();
  });

  it("names which placements the destination still served and what it said", async () => {
    // "still served: 3" is unactionable on its own. The operator has to know WHICH object is still there and what
    // answer kept it, or they cannot tell a live object from a destination that is lying to them.
    renderDrain({
      placements: [placement()],
      passes: [summary({
        examined: 2, abandoned: 0, stillServed: 1, unanswered: 1, remaining: 2, stoppedBy: null,
        outcomes: [
          { locationId: "location-1", objectKey: "artifacts/aaaa", outcome: "StillServed", detail: "HEAD 200" },
          { locationId: "location-2", objectKey: "artifacts/bbbb", outcome: "Unanswered", detail: "Throttled" },
        ],
      })],
    });

    await clickAbandon();

    const outcomes = await screen.findByRole("list", { name: "What this pass established" });
    expect(within(outcomes).getByText(/artifacts\/aaaa/)).toHaveTextContent("HEAD 200");
    expect(within(outcomes).getByText(/artifacts\/bbbb/)).toHaveTextContent("Throttled");
  });

  it("offers another pass while repeating can still reduce what is left", async () => {
    renderDrain({
      placements: [placement()],
      passes: [summary({ examined: 2, abandoned: 1, stillServed: 1, unanswered: 0, remaining: 4 })],
    });

    await clickAbandon();

    await waitFor(() => expect(screen.getByText(/4 still to release/)).toBeInTheDocument());
    expect(drainButton()).toBeEnabled();
  });

  it("still offers another pass after one that closed some records reached the end of what is held", async () => {
    // The ordinary partly-successful pass, and the common case: some records closed, one refused, and nothing left
    // ordered behind it. That it answered AT ALL is the whole separation from the batch that answered nothing — the
    // pass rotates every row it examined to the back, so the refuser it met is not what the next pass meets first.
    // Read as stuck, a pass that closed two of four records goes out under "fix the destination" with its control
    // dead, and the two it would have closed next stay recorded against a profile that can never retire.
    renderDrain({
      placements: [placement()],
      passes: [summary({
        examined: 4, abandoned: 2, stillServed: 1, unanswered: 1, remaining: 2, stoppedBy: null,
        outcomes: [
          { locationId: "location-1", objectKey: "artifacts/aaaa", outcome: "Abandoned", detail: "no such key" },
          { locationId: "location-2", objectKey: "artifacts/bbbb", outcome: "Abandoned", detail: "no such key" },
          { locationId: "location-3", objectKey: "artifacts/cccc", outcome: "StillServed", detail: "HEAD 200" },
          { locationId: "location-4", objectKey: "artifacts/dddd", outcome: "Unanswered", detail: "Throttled" },
        ],
      })],
    });

    await clickAbandon();

    await waitFor(() => expect(screen.getByText(/2 still to release/)).toBeInTheDocument());
    expect(drainButton()).toBeEnabled();
    expect(screen.queryByText(/Fix the destination/i)).not.toBeInTheDocument();
  });

  it("still offers another pass after the destination stopped one, because the rows behind the refusers are what it reaches next", async () => {
    // The batch is ordered least-recently-touched first precisely so placements that always refuse rotate BEHIND the
    // ones a pass never reached. Refusing the repeat strands the operator in front of five permanent refusers while
    // the fifteen drainable placements ordered behind them are the ones the next pass would have closed.
    renderDrain({
      placements: [placement()],
      passes: [summary({
        examined: 5, abandoned: 0, stillServed: 0, unanswered: 5, remaining: 20, stoppedBy: "CredentialInvalid",
        outcomes: unanswered(5, "CredentialInvalid"),
      })],
    });

    await clickAbandon();

    await waitFor(() => expect(screen.getByText(/answered CredentialInvalid for much of the batch/)).toBeInTheDocument());
    expect(screen.getByText(/rows this one never reached/i)).toBeInTheDocument();
    expect(drainButton()).toBeEnabled();
  });

  // Two drains racing, which the server deliberately refuses to read as a destination fault. Stopping here sends the
  // operator to repair a destination that never spoke and is working. The race reaches the UI on two carriers, and
  // the absent one only means this because the server names every refusal it could otherwise be confused with.
  it.each([
    { carrier: "had already been settled, so nothing came back to name", detail: null },
    { carrier: "was taken and then lost, which the server names", detail: "StaleWorker" },
  ])("still offers another pass when every claim in the batch $carrier", async ({ detail }) => {
    renderDrain({
      placements: [placement()],
      passes: [summary({
        examined: 4, abandoned: 0, stillServed: 0, unanswered: 4, remaining: 4, stoppedBy: null,
        outcomes: unanswered(4, detail),
      })],
    });

    await clickAbandon();

    await waitFor(() => expect(screen.getByText(/Another drain is holding some of these placements/i)).toBeInTheDocument());
    expect(screen.queryByText(/Fix the destination/i)).not.toBeInTheDocument();
    expect(drainButton()).toBeEnabled();
  });

  it("still offers another pass when nothing answered but placements were left unreached", async () => {
    renderDrain({
      placements: [placement()],
      passes: [summary({
        examined: 4, abandoned: 0, stillServed: 0, unanswered: 4, remaining: 10, stoppedBy: null,
        outcomes: unanswered(4, "Throttled"),
      })],
    });

    await clickAbandon();

    await waitFor(() => expect(screen.getByText(/10 still to release/)).toBeInTheDocument());
    expect(drainButton()).toBeEnabled();
  });

  it("refuses another pass when nothing answered and nothing was ordered behind it", async () => {
    // The one shape repeating cannot reduce: every placement the pass examined refused, and the pass reached the end
    // of the population, so the next one asks the same rows the same question.
    renderDrain({
      placements: [placement()],
      passes: [summary({
        examined: 4, abandoned: 0, stillServed: 0, unanswered: 4, remaining: 4, stoppedBy: null,
        outcomes: unanswered(4, "CredentialInvalid"),
      })],
    });

    await clickAbandon();

    await waitFor(() => expect(drainButton()).toBeDisabled());
    expect(screen.getByText(/Nothing this pass asked answered/i)).toBeInTheDocument();
  });

  it("refuses another pass when the breaker stopped it on the last placement still held", async () => {
    // The breaker fires AFTER each answer, so a population small enough to fit one batch can trip it on its final
    // row: the pass both stopped early and reached the end. Reading the stop first keeps the control live under a
    // note that promises "rows this one never reached" when there are none — an operator pressing forever.
    renderDrain({
      placements: [placement()],
      passes: [summary({
        examined: 5, abandoned: 0, stillServed: 0, unanswered: 5, remaining: 5, stoppedBy: "CredentialInvalid",
        outcomes: unanswered(5, "CredentialInvalid"),
      })],
    });

    await clickAbandon();

    await waitFor(() => expect(drainButton()).toBeDisabled());
    expect(screen.getByText(/Nothing this pass asked answered/i)).toBeInTheDocument();
    expect(screen.queryByText(/rows this one never reached/i)).not.toBeInTheDocument();
  });

  it("still offers another pass when a pass that answered left work behind", async () => {
    // A pass where every row was still served is NOT stuck: an examined placement rotates behind the ones the pass
    // never reached, so the next pass looks at different rows.
    renderDrain({
      placements: [placement()],
      passes: [summary({ examined: 3, abandoned: 0, stillServed: 3, unanswered: 0, remaining: 7, stoppedBy: null, outcomes: [] })],
    });

    await clickAbandon();

    await waitFor(() => expect(screen.getByText(/7 still to release/)).toBeInTheDocument());
    expect(drainButton()).toBeEnabled();
  });

  it("says so when the profile holds nothing, rather than offering a pass over an empty population", async () => {
    renderDrain({ totals: [], placements: [] });

    expect(await screen.findByText(/holds no placements/i)).toBeInTheDocument();
    expect(drainButton()).toBeDisabled();
  });
});
