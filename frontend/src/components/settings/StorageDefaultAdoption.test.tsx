import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { StorageAdoptionStatus } from "@/api/storageAdoptions";

import { StorageDefaultAdoption } from "./StorageDefaultAdoption";

const offered: StorageAdoptionStatus = {
  dataClassTypeKey: "agent-run-log/v1",
  displayName: "Agent run logs",
  defaultAvailable: true,
  adopted: false,
  teamOwnsRoute: false,
  canAdopt: true,
  adoptionIsIrreversible: false,
  sourceRevision: null,
  templateRevision: 3,
};

type PostHandler = (body: unknown) => Response;

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/**
 * Renders the card alone. The page it lives on is covered by its own suite; what needs proving here is
 * what this card offers, what it refuses to offer, and what it says about each outcome the server names.
 */
function renderCard(adoptions: StorageAdoptionStatus[], options: { mayManage?: boolean; onPost?: PostHandler } = {}) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", "team-1");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
    if (path === "/api/storage/adoptions" && (init.method ?? "GET") === "GET") return json(adoptions);
    if (path === "/api/storage/adoptions") return options.onPost?.(JSON.parse(String(init.body))) ?? json({});
    return json([]);
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });

  return render(<QueryClientProvider client={client}><StorageDefaultAdoption mayManage={options.mayManage ?? true} /></QueryClientProvider>);
}

const card = () => screen.findByRole("region", { name: "Deployment defaults" });

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("StorageDefaultAdoption", () => {
  it("says nothing at all when the deployment has authored no default", async () => {
    // Not an empty panel. An empty scope card would imply an absence this screen expects someone to fill
    // from here, and nobody can: templates are authored elsewhere, under a different capability.
    renderCard([]);

    await waitFor(() => expect(vi.mocked(globalThis.fetch)).toHaveBeenCalled());
    expect(screen.queryByRole("region", { name: "Deployment defaults" })).not.toBeInTheDocument();
  });

  it("shows the consequence before taking the default, not as the same click", async () => {
    let posted: unknown;
    renderCard([{ ...offered, adoptionIsIrreversible: true }], {
      onPost: (body) => { posted = body; return json({ outcome: "Adopted", storageProfileId: "p1", storageRouteId: "r1", sourceRevision: 3, detail: null }); },
    });

    fireEvent.click(await screen.findByRole("button", { name: "Use this" }));

    expect(await screen.findByRole("group", { name: /Agent run logs/ })).toHaveTextContent(/never be turned off/i);
    expect(posted).toBeUndefined();

    fireEvent.click(screen.getByRole("button", { name: "Use this default" }));

    await waitFor(() => expect(posted).toEqual({ dataClassTypeKey: "agent-run-log/v1" }));
    expect(await screen.findByRole("status")).toHaveTextContent(/now goes to the deployment default/i);
  });

  it("reports a refused destination with the provider's own reason and does not claim success", async () => {
    // The server answers 200 for this precisely so the screen can distinguish it. Rendering it as a
    // failed request would throw away the one thing the operator needs: which end to go and fix.
    renderCard([offered], {
      onPost: () => json({ outcome: "DestinationUnusable", storageProfileId: null, storageRouteId: null, sourceRevision: null, detail: "The destination accepted a read but refused a write." }),
    });

    fireEvent.click(await screen.findByRole("button", { name: "Use this" }));
    fireEvent.click(screen.getByRole("button", { name: "Use this default" }));

    const status = await screen.findByRole("status");
    expect(status).toHaveTextContent(/refused a write/i);
    expect(status).not.toHaveTextContent(/now goes to/i);
  });

  it("offers nothing to a team that chose its own destination, and says why", async () => {
    renderCard([{ ...offered, canAdopt: false, teamOwnsRoute: true }]);

    expect(await card()).toHaveTextContent(/already points this data somewhere it chose/i);
    expect(screen.queryByRole("button", { name: "Use this" })).not.toBeInTheDocument();
  });

  it("tells an adopted team a newer default exists without implying its data moved", async () => {
    // What is already stored resolves through the profile revision recorded when it was written, so the
    // sentence has to carry that or it reads as data loss.
    renderCard([{ ...offered, canAdopt: false, adopted: true, sourceRevision: 2, templateRevision: 3 }]);

    const region = await card();
    expect(region).toHaveTextContent(/earlier version of the deployment default/i);
    expect(region).toHaveTextContent(/stays where it was written/i);
  });

  it("shows a reader the state but no way to change it", async () => {
    renderCard([offered], { mayManage: false });

    expect(await card()).toHaveTextContent(/Available to this team/i);
    expect(screen.queryByRole("button", { name: "Use this" })).not.toBeInTheDocument();
  });
});
