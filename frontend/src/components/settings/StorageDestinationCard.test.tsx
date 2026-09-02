import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ProfilePlacementTotal, StorageCredentialMetadata, StorageProfileDetail, StorageProfileSummary, StorageProviderModuleSummary } from "@/api/storage";
import type { RoutedDataClass, StorageRouteSummary } from "@/api/storageRoutes";

import { StorageDestinationCard } from "./StorageDestinationCard";

const provider: StorageProviderModuleSummary = {
  typeKey: "aliyun-oss/v1",
  displayName: "Aliyun OSS",
  // Declaration order is what the card reads, so the address reads endpoint-then-bucket-then-prefix here.
  configSchema: { type: "object", properties: { endpoint: { type: "string" }, bucket: { type: "string" }, keyPrefix: { type: "string" } } },
  secretSchema: { type: "object", properties: { accessKeyId: { type: "string" } }, required: ["accessKeyId"] },
  capabilities: [],
  teamNamespaceProperty: "bucket",
};

const profile: StorageProfileSummary = {
  id: "p1",
  stableName: "codespace-artifacts",
  state: "Active",
  currentRevision: 3,
  xmin: 11,
  providerTypeKey: provider.typeKey,
  createdDate: "2026-08-28T08:57:00Z",
  lastModifiedDate: "2026-09-01T14:22:00Z",
  health: { status: "Available", writeVerified: true, profileRevision: 3, latencyMilliseconds: 214, observedAt: new Date().toISOString() },
};

const detail: StorageProfileDetail = {
  id: profile.id,
  stableName: profile.stableName,
  state: "Active",
  currentRevision: 3,
  xmin: 11,
  createdDate: profile.createdDate,
  createdBy: "u1",
  lastModifiedDate: profile.lastModifiedDate,
  lastModifiedBy: "u1",
  revisions: [{
    id: "r3",
    revision: 3,
    providerTypeKey: provider.typeKey,
    nonSecretConfig: { endpoint: "oss-cn-hongkong.aliyuncs.com", bucket: "codespace-artifacts", keyPrefix: "teams/acme/" },
    credentialRef: "db:cred-b:2",
    namespaceFingerprint: "sha256:x",
    createdDate: profile.lastModifiedDate,
    createdBy: "u1",
  }],
};

const credentials: StorageCredentialMetadata[] = [
  { id: "cred-a", stableName: "old-key", state: "Active", currentRevision: 1, providerTypeKey: provider.typeKey, safeHint: "LTAIold…0000", credentialRef: "db:cred-a:1", createdDate: profile.createdDate, currentRevisionCreatedDate: profile.createdDate, xmin: 4 },
  { id: "cred-b", stableName: "current-key", state: "Active", currentRevision: 2, providerTypeKey: provider.typeKey, safeHint: "LTAI5tE…q7Xk", credentialRef: "db:cred-b:2", createdDate: profile.createdDate, currentRevisionCreatedDate: profile.createdDate, xmin: 9 },
];

const dataClasses: RoutedDataClass[] = [
  { typeKey: "workflow-artifact/v1", displayName: "Workflow artifacts", hasLocalFallback: true },
  { typeKey: "agent-run-log/v1", displayName: "Agent run logs", hasLocalFallback: false },
];

function route(overrides: Partial<StorageRouteSummary> = {}): StorageRouteSummary {
  return {
    id: "rt1", dataClassTypeKey: "workflow-artifact/v1", state: "Active", currentRevision: 1, xmin: 3,
    storageProfileId: profile.id, storageProfileStableName: profile.stableName,
    profileRevisionMode: "CurrentAtWrite", pinnedProfileRevision: null,
    createdDate: profile.createdDate, lastModifiedDate: profile.lastModifiedDate, ...overrides,
  };
}

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function renderCard(options: { profile?: StorageProfileSummary; routes?: StorageRouteSummary[]; totals?: ProfilePlacementTotal[] } = {}) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
    if (path === `/api/storage/profiles/${profile.id}`) return json(detail);
    if (path === `/api/storage/profiles/${profile.id}/placements/totals`) return json(options.totals ?? []);
    return json([]);
  }));

  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <StorageDestinationCard
        profile={options.profile ?? profile}
        providers={[provider]}
        credentials={credentials}
        routes={options.routes ?? [route()]}
        dataClasses={dataClasses}
        mayManage
        onFix={() => {}}
        onEditRouting={() => {}}
        onAdvanced={() => {}}
      />
    </QueryClientProvider>,
  );
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("StorageDestinationCard", () => {
  // Built from the provider's own schema, in its own declaration order, so a provider that ships tomorrow reads
  // correctly with no change here — and a provider whose fields are not endpoint/bucket does not read as gibberish.
  it("builds the address out of the provider's own configuration schema", async () => {
    renderCard();

    expect(await screen.findByText("Aliyun OSS · oss-cn-hongkong.aliyuncs.com · codespace-artifacts · teams/acme/")).toBeInTheDocument();
  });

  // Purged and Deleted bytes are gone. Counting them would tell an operator this destination holds data it does not,
  // which is the one number they would decide a retirement on.
  it("counts only what a read could still open", async () => {
    renderCard({ totals: [
      { state: "Available", count: 1284, sizeBytes: 3_435_973_837 },
      { state: "Purged", count: 900, sizeBytes: 9_000_000_000 },
      { state: "Deleted", count: 12, sizeBytes: 1_000 },
    ] });

    expect(await screen.findByText("1,284 objects · 3.2 GB")).toBeInTheDocument();
  });

  it("says nothing is here rather than showing a zero", async () => {
    renderCard({ totals: [{ state: "Purged", count: 5, sizeBytes: 500 }] });

    expect(await screen.findAllByText("Nothing yet")).not.toHaveLength(0);
  });

  // The pointer names an EXACT key version and the runtime never falls forward. A card that showed whichever key of
  // the right provider happened to be active would name the key an operator then replaces — breaking a destination
  // that was working.
  it("names the key the destination actually points at, not the first active one", async () => {
    renderCard();

    expect(await screen.findByText("LTAI5tE…q7Xk")).toBeInTheDocument();
    expect(screen.queryByText("LTAIold…0000")).not.toBeInTheDocument();
  });

  it("lists what lands here by the data class's own name, and only for an active route", async () => {
    renderCard({ routes: [route(), route({ id: "rt2", dataClassTypeKey: "agent-run-log/v1", state: "Disabled" })] });

    expect(await screen.findByText("Workflow artifacts")).toBeInTheDocument();
    expect(screen.queryByText(/Agent run logs/)).not.toBeInTheDocument();
  });

  // A red chip says something is wrong; only the sentence says which end to fix, and only the button makes it
  // one click away instead of a hunt through a menu.
  it("offers the repair, and says which end to fix, when writes are failing", async () => {
    renderCard({ profile: { ...profile, health: { status: "Unavailable", writeVerified: false, profileRevision: 3, latencyMilliseconds: 90, observedAt: new Date().toISOString(), failureStage: "Probe", failureCode: "ProbePermissionDenied" } } });

    expect(await screen.findByRole("button", { name: "Fix the connection" })).toBeInTheDocument();
    expect(screen.getByText(/policy does not allow writing here/i)).toBeInTheDocument();
    expect(screen.getByText(/Probe \/ ProbePermissionDenied/)).toBeInTheDocument();
  });

  it("offers no repair while the destination is answering", async () => {
    renderCard();

    await screen.findByText(/Aliyun OSS/);
    expect(screen.queryByRole("button", { name: "Fix the connection" })).not.toBeInTheDocument();
  });
});
