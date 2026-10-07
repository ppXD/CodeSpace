import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { PackSummary, PackSyncResult } from "@/api/packs";

import { SyncResultModal } from "./SyncResultModal";

const TOKEN = "fake-pack-token-0123456789";
const NEW_SKILL = "skills/new-skill/SKILL.md";

const pack: PackSummary = {
  id: "pack-1", kind: "GitUrl", name: "acme/agents", url: "https://git.example.test/acme/agents", reference: "main",
  lastSyncedSha: null, lastSyncedDate: null, agentCount: 1, skillCount: 0,
};

const result: PackSyncResult = {
  packId: pack.id, reference: "main", upToDate: 1, updated: 0,
  newArtifacts: {
    reference: "main",
    agents: [],
    skills: [{ sourcePath: NEW_SKILL, name: "new-skill", derivedSlug: "new-skill", description: "Brand new.", body: "# New", category: null, rawFrontmatterJson: "{}", diagnostics: [], slugConflict: false, importable: true }],
  },
};

interface Call { path: string; method: string; body: string }

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function renderModal(shown: PackSummary) {
  const calls: Call[] = [];
  const onClose = vi.fn();
  localStorage.setItem("codespace.jwt", "test-jwt");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
    calls.push({ path, method: init.method ?? "GET", body: init.body ? String(init.body) : "" });
    return json({ packId: shown.id, items: [{ sourcePath: NEW_SKILL, kind: "Skill", outcome: "Imported", definitionId: "s1", reason: null }] });
  }));

  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <SyncResultModal pack={shown} result={result} onClose={onClose} />
    </QueryClientProvider>,
  );
  return { calls, onClose };
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("SyncResultModal", () => {
  // The add-after-sync used to post the pack's saved URL back to /api/agents/import-url. A private pack's URL no longer
  // carries the credential its clone needs, and the URL could resolve a different pack — so the add names the pack by id
  // and lets the server clone the pack's own sealed source.
  it("adds the selection to the synced pack by id and sends only the selection", async () => {
    const { calls, onClose } = renderModal(pack);

    fireEvent.click(screen.getByRole("button", { name: /Add 1/ }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(calls).toHaveLength(1);
    expect(calls[0].path).toBe("/api/packs/pack-1/import");
    expect(calls[0].method).toBe("POST");
    expect(JSON.parse(calls[0].body)).toEqual({ sourcePaths: [NEW_SKILL] });
  });

  it("never sends the pack's URL, even one cached from before the server stripped its credential", async () => {
    const { calls, onClose } = renderModal({ ...pack, url: `https://x-access-token:${TOKEN}@git.example.test/acme/agents` });

    fireEvent.click(screen.getByRole("button", { name: /Add 1/ }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(calls.map((c) => c.path)).not.toContain("/api/agents/import-url");
    expect(calls.map((c) => c.body).join("\n")).not.toContain(TOKEN);
    expect(Object.keys(JSON.parse(calls[0].body))).toEqual(["sourcePaths"]);
  });
});
