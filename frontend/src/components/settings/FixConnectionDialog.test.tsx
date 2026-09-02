import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { StorageCredentialMetadata, StorageProfileDetail, StorageProfileSummary, StorageProviderModuleSummary } from "@/api/storage";

import { FixConnectionDialog } from "./FixConnectionDialog";

const provider: StorageProviderModuleSummary = {
  typeKey: "aliyun-oss/v1",
  displayName: "Aliyun OSS",
  configSchema: { type: "object", properties: { endpoint: { type: "string", title: "Endpoint" }, bucket: { type: "string", title: "Bucket name" } }, required: ["endpoint", "bucket"] },
  secretSchema: { type: "object", properties: { accessKeyId: { type: "string", title: "AccessKey ID" }, accessKeySecret: { type: "string", title: "AccessKey secret", writeOnly: true } }, required: ["accessKeyId", "accessKeySecret"] },
  capabilities: [],
  teamNamespaceProperty: "bucket",
};

const profile: StorageProfileSummary = {
  id: "p1", stableName: "codespace-artifacts", state: "Active", currentRevision: 3, xmin: 11,
  providerTypeKey: provider.typeKey, createdDate: "2026-08-28T08:57:00Z", lastModifiedDate: "2026-09-01T14:22:00Z", health: null,
};

const detail: StorageProfileDetail = {
  id: profile.id, stableName: profile.stableName, state: "Active", currentRevision: 3, xmin: 11,
  createdDate: profile.createdDate, createdBy: "u1", lastModifiedDate: profile.lastModifiedDate, lastModifiedBy: "u1",
  revisions: [{
    id: "r3", revision: 3, providerTypeKey: provider.typeKey,
    nonSecretConfig: { endpoint: "oss-cn-hongkong.aliyuncs.com", bucket: "codespace-artifacts" },
    credentialRef: "db:cred-b:2", namespaceFingerprint: "sha256:x", createdDate: profile.lastModifiedDate, createdBy: "u1",
  }],
};

const credentials: StorageCredentialMetadata[] = [
  { id: "cred-a", stableName: "unrelated", state: "Active", currentRevision: 5, providerTypeKey: provider.typeKey, safeHint: "other", credentialRef: "db:cred-a:5", createdDate: profile.createdDate, currentRevisionCreatedDate: profile.createdDate, xmin: 4 },
  { id: "cred-b", stableName: "current-key", state: "Active", currentRevision: 2, providerTypeKey: provider.typeKey, safeHint: "LTAI5tE…q7Xk", credentialRef: "db:cred-b:2", createdDate: profile.createdDate, currentRevisionCreatedDate: profile.createdDate, xmin: 9 },
];

interface Call { path: string; body: unknown }

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function renderDialog(options: { probeStatus?: string } = {}) {
  const calls: Call[] = [];
  localStorage.setItem("codespace.jwt", "test-jwt");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
    if ((init.method ?? "GET") !== "GET") calls.push({ path, body: init.body ? JSON.parse(String(init.body)) : null });

    if (path === `/api/storage/profiles/${profile.id}`) return json(detail);
    if (path === "/api/storage/probes" || path === `/api/storage/profiles/${profile.id}/probe`) {
      return json({ providerTypeKey: provider.typeKey, profileId: profile.id, profileRevision: 3, writeAccessRequested: true, status: options.probeStatus ?? "Available", latencyMilliseconds: 180, failure: options.probeStatus && options.probeStatus !== "Available" ? { stage: "Probe", code: "ProbeSignatureMismatch", retryable: false } : null });
    }
    if (path === "/api/storage/credentials/cred-b/revisions") return json({ ...credentials[1], currentRevision: 3, credentialRef: "db:cred-b:3", xmin: 99 });
    if (path === `/api/storage/profiles/${profile.id}/revisions`) return json({ ...detail, currentRevision: 4 });
    return json([]);
  }));

  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FixConnectionDialog profile={profile} provider={provider} credentials={credentials} onClose={() => {}} />
    </QueryClientProvider>,
  );
  return calls;
}

function fillSecret() {
  fireEvent.change(screen.getByLabelText("AccessKey ID"), { target: { value: "LTAI5tNew" } });
  fireEvent.change(screen.getByLabelText("AccessKey secret"), { target: { value: "new-secret" } });
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("FixConnectionDialog", () => {
  // The trap this dialog exists to close. Both writes are needed and neither alone does anything: the destination
  // names an EXACT key version and never falls forward, so a rotated key that nothing repoints at changes nothing at
  // runtime. That is what made operators rebuild the whole destination instead of repairing it.
  it("rotates the key AND repoints the destination at the new version", async () => {
    const calls = renderDialog();
    await screen.findByLabelText("AccessKey secret");
    fillSecret();

    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await screen.findByText("It answered.");
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(calls.filter((call) => call.path.endsWith("/revisions"))).toHaveLength(2));
    const rotate = calls.find((call) => call.path === "/api/storage/credentials/cred-b/revisions");
    const repoint = calls.find((call) => call.path === `/api/storage/profiles/${profile.id}/revisions`);
    expect(rotate?.body).toMatchObject({ expectedXmin: 9, expectedCurrentRevision: 2 });
    expect(repoint?.body).toMatchObject({ expectedXmin: 11, expectedCurrentRevision: 3, credentialRef: "db:cred-b:3" });
  });

  // Nothing is written before the destination answers — the same promise the add flow makes, for the same reason:
  // neither of these two rows can be deleted afterwards.
  it("writes nothing until the test passes", async () => {
    const calls = renderDialog({ probeStatus: "Unavailable" });
    await screen.findByLabelText("AccessKey secret");
    fillSecret();

    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));

    expect(await screen.findByText(/rejected the request signature/i)).toBeInTheDocument();
    expect(screen.getByText(/Nothing was changed/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(calls.filter((call) => call.path.endsWith("/revisions"))).toHaveLength(0);
  });

  // Two different questions, and the dialog has to ask the right one. Keeping the stored key there is nothing
  // unsaved to qualify, so the destination AS IT STANDS is what gets asked — which is the question when it was the
  // address that was wrong, not the key.
  it("asks the saved destination when the key is being kept", async () => {
    const calls = renderDialog();
    await screen.findByLabelText("AccessKey secret");

    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));

    await waitFor(() => expect(calls.some((call) => call.path === `/api/storage/profiles/${profile.id}/probe`)).toBe(true));
    expect(calls.some((call) => call.path === "/api/storage/probes")).toBe(false);
  });

  // A replacement key is qualified WITHOUT being saved first, which is the whole reason a repair costs a retry
  // rather than a permanent row.
  it("asks the unsaved configuration when the key is being replaced", async () => {
    const calls = renderDialog();
    await screen.findByLabelText("AccessKey secret");
    fillSecret();

    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));

    await waitFor(() => expect(calls.some((call) => call.path === "/api/storage/probes")).toBe(true));
    expect(calls.some((call) => call.path === `/api/storage/profiles/${profile.id}/probe`)).toBe(false);
  });

  // A passing test on a destination nobody edited is good news, not a change to make. Offering Save would append a
  // revision that says nothing, growing a ledger this surface exists to keep out of sight.
  it("has nothing to save when the destination already works and nothing was edited", async () => {
    renderDialog();
    await screen.findByLabelText("AccessKey secret");  // a labelled password input, unlike SchemaForm's contenteditable string fields

    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));

    expect(await screen.findByText(/Nothing needs changing/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("says the old key is kept, because data stored here still opens through it", async () => {
    renderDialog();

    expect(await screen.findByText(/the old one is kept/i)).toBeInTheDocument();
  });
});
