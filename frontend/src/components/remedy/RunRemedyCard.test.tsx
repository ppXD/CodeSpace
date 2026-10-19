import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ProviderRemedy } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import { RunOpenContext } from "@/components/workflows/runOpenContext";

import { RunRemedyCard } from "./RunRemedyCard";

/**
 * The remediation card for a run page. For a failure carried up from a sub-workflow's child run the page the card is on is the PARENT's: the
 * remedy is read for the parent's run id, the link opens the child run the failure happened in, and Re-run replays the PARENT — which starts a
 * fresh child — never the child the failure happened in.
 */
const PARENT = "11111111-1111-4111-8111-111111111111";
const CHILD = "1a2b3c4d-0000-4000-8000-000000000000";
const REPLAY = "99999999-9999-4999-8999-999999999999";

const remedy: ProviderRemedy = {
  code: "credential_needs_reconnect",
  title: "GitLab no longer accepts the credential this run used",
  cause: "This failed in the sub-workflow “Review the pull requests” (run 1a2b3c4d), at node 'list'. GitLab refused to renew the sign-in behind “GitLab · nacho”.",
  resolved: true,
  credential: { id: "c1", displayName: "GitLab · nacho", ownership: "Personal", ownerUserId: "u1", ownerName: "Nacho", authType: "OAuth", status: "Active", health: "active", retired: false, providerInstanceId: "pi1" },
  repositories: [{ id: "r1", fullPath: "acme/api", status: "Active", credentialId: "c1", credentialWorks: true }],
  providerInstance: null,
  origin: { runId: CHILD, workflowId: "w-child", workflowName: "Review the pull requests", nodeId: "list" },
  actions: [{ kind: "rerun", label: "Re-run", enabled: true }, { kind: "open_providers", label: "Open Providers", enabled: true }],
  askPeople: [],
};

type Call = { method: string; url: string };

function renderCard(props: { onOpenRun?: (runId: string) => void; context?: ((runId: string) => void) | null } = {}) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", "t1");

  const calls: Call[] = [];

  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    const method = init?.method ?? "GET";

    calls.push({ method, url });

    const body = url.endsWith(`/api/workflows/runs/${PARENT}/remedy`) ? remedy : url.endsWith(`/api/workflows/runs/${PARENT}/replay`) ? { runId: REPLAY } : undefined;

    return new Response(body === undefined ? "" : JSON.stringify(body), { status: body === undefined ? 404 : 200, headers: { "Content-Type": "application/json" } });
  }));

  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })}>
      <DialogProvider>
        <RunOpenContext.Provider value={props.context ?? null}>
          <RunRemedyCard runId={PARENT} onOpenRun={props.onOpenRun} />
        </RunOpenContext.Provider>
      </DialogProvider>
    </QueryClientProvider>,
  );

  return calls;
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe("RunRemedyCard — a failure carried up from a sub-workflow's child run", () => {
  it("reads the remedy for the run the page is on and links to the child run it happened in", async () => {
    const onOpenRun = vi.fn();
    const calls = renderCard({ onOpenRun });

    fireEvent.click(await screen.findByRole("button", { name: "In sub-workflow Review the pull requests (run 1a2b3c4d)" }));

    expect(onOpenRun).toHaveBeenCalledWith(CHILD);
    expect(calls.filter((c) => c.url.includes("/remedy")).map((c) => c.url)).toEqual([expect.stringContaining(`/api/workflows/runs/${PARENT}/remedy`)]);
  });

  it("opens the child through the run viewer's context when the page was not handed a handler", async () => {
    const context = vi.fn();

    renderCard({ context });

    fireEvent.click(await screen.findByRole("button", { name: /In sub-workflow Review the pull requests/ }));

    expect(context).toHaveBeenCalledWith(CHILD);
  });

  it("names the run without a link where no run can be opened", async () => {
    renderCard();

    expect(await screen.findByText("In sub-workflow Review the pull requests (run 1a2b3c4d)")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /In sub-workflow/ })).toBeNull();
  });

  it("re-runs the PARENT — the run the page is on — and opens the run that starts, never the child the failure happened in", async () => {
    const onOpenRun = vi.fn();
    const calls = renderCard({ onOpenRun });

    fireEvent.click(await screen.findByRole("button", { name: "Re-run" }));

    await waitFor(() => expect(onOpenRun).toHaveBeenCalledWith(REPLAY));

    const replays = calls.filter((c) => c.method === "POST");

    expect(replays.map((c) => c.url)).toEqual([expect.stringContaining(`/api/workflows/runs/${PARENT}/replay`)]);
    expect(calls.some((c) => c.url.includes(CHILD))).toBe(false);   // nothing is asked of, or done to, the child run
    expect(onOpenRun).not.toHaveBeenCalledWith(CHILD);
  });
});
