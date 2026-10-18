import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { DiagnosticBlock } from "@/api/sessions";
import type { ProviderRemedy } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import { RunActionsContext } from "@/components/workflows/runActionsContext";
import { ErrorCard } from "./SessionRoomView";

/**
 * A run that failed on a provider connection (a credential disconnected, refused, a redirecting address) carries the failure's code on
 * its diagnostic. The Room's turn is cached and shared, so it holds only the CODE; what to do about it, and who may, is read live
 * for whoever is looking — and the diagnostic is the whole story until that answer arrives, or when there is none.
 */
const RUN = "11111111-1111-1111-1111-111111111111";

const diag = (over: Partial<DiagnosticBlock> = {}): DiagnosticBlock => ({
  id: "turn-1:diagnostic", seq: 5, type: "diagnostic", tone: "Error",
  title: "The credential this run used was disconnected",
  text: "The credential 'GitLab · NACHO.L' was disconnected, so nothing bound through it can authenticate.",
  actions: [], rawDetail: "Node 'list' failed.", failureCode: "credential_disconnected", ...over,
});

const remedy: ProviderRemedy = {
  code: "credential_disconnected",
  title: "The credential this run used was disconnected",
  cause: "“GitLab · NACHO.L” was disconnected, so acme/api cannot authenticate with GitLab. Reconnect it to put it back, or link acme/api to a credential that works.",
  resolved: false,
  credential: { id: "c1", displayName: "GitLab · NACHO.L", ownership: "Personal", ownerUserId: "u9", ownerName: "Nacho", authType: "OAuth", status: "Revoked", health: "revoked", retired: false, providerInstanceId: "pi1" },
  repositories: [{ id: "r1", fullPath: "acme/api", status: "Error", credentialId: "c1", credentialWorks: false }],
  providerInstance: { id: "pi1", displayName: "Acme GitLab", provider: "GitLab", baseUrl: "http://gitlab.test" },
  actions: [
    { kind: "reconnect", label: "Reconnect GitLab", enabled: false, disabledReason: "Only Nacho, or a team Admin, can do this." },
    { kind: "relink", label: "Re-link repository", enabled: false, disabledReason: "Needs the Admin or Owner role." },
    { kind: "open_providers", label: "Open Providers", enabled: true },
    { kind: "rerun", label: "Re-run", enabled: false, disabledReason: "Fix the connection first, then run it again." },
  ],
  askPeople: [{ userId: "u9", name: "Nacho", role: "Owner of the credential" }, { userId: "u3", name: "Olive", role: "Owner" }],
};

function renderCard(block: DiagnosticBlock, answer: { status: number; body?: unknown }, withRun = true) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", "t1");

  const urls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    urls.push(url);

    return url.includes("/remedy") ? new Response(answer.body === undefined ? null : JSON.stringify(answer.body), { status: answer.status, headers: { "Content-Type": "application/json" } }) : new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } });
  }));

  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })}>
      <DialogProvider>
        <RunActionsContext.Provider value={withRun ? { runId: RUN, isTerminal: true } : null}>
          <ErrorCard diag={block} />
        </RunActionsContext.Provider>
      </DialogProvider>
    </QueryClientProvider>,
  );

  return urls;
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("the room's diagnostic for a provider-connection failure", () => {
  it("shows the failure's own words first, then the remediation card once the live remedy arrives", async () => {
    renderCard(diag(), { status: 200, body: remedy });

    expect(screen.getByText(/was disconnected, so nothing bound through it can authenticate/)).toBeTruthy();   // the diagnostic, before anything is known

    await waitFor(() => expect(screen.getByRole("alert", { name: "Connection problem" })).toBeTruthy());

    expect(screen.getByText(/so acme\/api cannot authenticate with GitLab/)).toBeTruthy();
    expect(screen.getByRole("note").textContent).toContain("Ask Nacho (Owner of the credential) or Olive (Owner).");
    expect(screen.queryByText(/Fix credentials/)).toBeNull();
  });

  it("asks for the remedy of THIS turn's run", async () => {
    const urls = renderCard(diag(), { status: 200, body: remedy });

    await waitFor(() => expect(urls.some((u) => u.includes(`/api/workflows/runs/${RUN}/remedy`))).toBe(true));
  });

  it("keeps the ordinary diagnostic when the backend has no remedy for the run", async () => {
    const urls = renderCard(diag(), { status: 204 });

    await waitFor(() => expect(urls.some((u) => u.includes("/remedy"))).toBe(true));

    expect(screen.queryByRole("alert", { name: "Connection problem" })).toBeNull();
    expect(screen.getByText("The credential this run used was disconnected")).toBeTruthy();
    expect(screen.getByText(/nothing bound through it can authenticate/)).toBeTruthy();
  });

  it("keeps the ordinary diagnostic when the remedy cannot be read", async () => {
    const urls = renderCard(diag(), { status: 500, body: { code: "internal_error", message: "boom" } });

    await waitFor(() => expect(urls.some((u) => u.includes("/remedy"))).toBe(true));

    expect(screen.queryByRole("alert", { name: "Connection problem" })).toBeNull();
    expect(screen.getByText(/nothing bound through it can authenticate/)).toBeTruthy();
  });

  it("never asks for a remedy for a diagnostic that carries no failure code — a run recorded before they were kept reads as it did", () => {
    const urls = renderCard(diag({ failureCode: null, title: "Authentication failed" }), { status: 200, body: remedy });

    expect(screen.getByText("Authentication failed")).toBeTruthy();
    expect(urls.filter((u) => u.includes("/remedy"))).toEqual([]);
  });

  it("never asks without a run to ask about", () => {
    const urls = renderCard(diag(), { status: 200, body: remedy }, false);

    expect(urls.filter((u) => u.includes("/remedy"))).toEqual([]);
    expect(screen.getByText(/nothing bound through it can authenticate/)).toBeTruthy();
  });
});
