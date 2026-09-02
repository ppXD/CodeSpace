import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { StorageCredentialMetadata, StorageProfileDetail, StorageProfileProbeResult, StorageProfileSummary, StorageProviderModuleSummary } from "@/api/storage";
import type { StorageAdoptionStatus } from "@/api/storageAdoptions";
import type { StorageRouteSummary } from "@/api/storageRoutes";
import type { MeResponse } from "@/api/types";
import { TeamPermissions } from "@/hooks/use-team-management";

import { StorageSettings } from "./StorageSettings";

const localProvider: StorageProviderModuleSummary = {
  typeKey: "local-rwx/v1",
  displayName: "Local / shared filesystem",
  configSchema: {
    type: "object",
    properties: { rootPath: { type: "string", title: "Root path" } },
    required: ["rootPath"],
    additionalProperties: false,
  },
  secretSchema: { type: "object", properties: {}, additionalProperties: false },
  capabilities: ["ConditionalCreate", "StreamingRead"],
  teamNamespaceProperty: "rootPath",
};

const secretProvider: StorageProviderModuleSummary = {
  typeKey: "aliyun-oss/v1",
  displayName: "Aliyun OSS",
  configSchema: {
    type: "object",
    properties: { bucket: { type: "string", title: "Bucket" } },
    required: ["bucket"],
    additionalProperties: false,
  },
  secretSchema: {
    type: "object",
    properties: { accessKeySecret: { type: "string", title: "Access key secret", writeOnly: true } },
    required: ["accessKeySecret"],
    additionalProperties: false,
  },
  capabilities: ["MultipartUpload", "StreamingWrite"],
  teamNamespaceProperty: "keyPrefix",
};

const profile: StorageProfileSummary = {
  id: "profile-1",
  stableName: "primary",
  state: "Draft",
  currentRevision: 2,
  xmin: 17,
  providerTypeKey: localProvider.typeKey,
  createdDate: "2026-08-14T10:00:00Z",
  lastModifiedDate: "2026-08-15T10:00:00Z",
};

const credential: StorageCredentialMetadata = {
  id: "credential-1",
  stableName: "aliyun-primary",
  state: "Active",
  currentRevision: 3,
  xmin: 21,
  providerTypeKey: secretProvider.typeKey,
  safeHint: "AKID…7Q",
  credentialRef: "db:00000000-0000-0000-0000-000000000123:3",
  createdDate: "2026-08-14T09:00:00Z",
  currentRevisionCreatedDate: "2026-08-15T09:00:00Z",
  revokedDate: null,
};

const detail: StorageProfileDetail = {
  id: profile.id,
  stableName: profile.stableName,
  state: profile.state,
  currentRevision: profile.currentRevision,
  xmin: profile.xmin,
  createdDate: profile.createdDate,
  createdBy: "user-1",
  lastModifiedDate: profile.lastModifiedDate,
  lastModifiedBy: "user-1",
  revisions: [{
    id: "revision-2",
    revision: 2,
    providerTypeKey: localProvider.typeKey,
    nonSecretConfig: { rootPath: "/artifacts/old" },
    credentialRef: null,
    namespaceFingerprint: "sha256:hidden",
    createdDate: profile.lastModifiedDate,
    createdBy: "user-1",
  }],
};

type FetchHandler = (path: string, init: RequestInit) => Response | Promise<Response>;

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, statusText: status === 409 ? "Conflict" : "", headers: { "Content-Type": "application/json" } });
}

function page<T>(items: T[], nextCursor: string | null = null) {
  return { items, nextCursor };
}

function probeResult(overrides: Partial<StorageProfileProbeResult> = {}): StorageProfileProbeResult {
  return {
    profileId: profile.id,
    profileRevision: profile.currentRevision,
    writeAccessRequested: true,
    status: "Available",
    latencyMilliseconds: 14,
    failure: null,
    ...overrides,
  };
}

const routedDataClasses = [
  { typeKey: "agent-run-log/v1", displayName: "Agent run logs" },
  { typeKey: "workflow-artifact/v1", displayName: "Workflow artifacts" },
];

function me(permissions: string[]): MeResponse {
  return {
    id: "user-1", email: "owner@test.local", name: "Owner", passwordMustChange: false, permissions: [],
    teams: [{ id: "team-1", slug: "platform", name: "Platform", kind: "Workspace", role: "Owner", permissions, memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0 }],
  };
}

interface RenderOptions {
  /** Team-scoped permissions the server expands for this caller. Storage reads AND writes both require storage.manage. */
  permissions?: string[];
  routes?: StorageRouteSummary[];
  /** Deployment defaults offered to this team. Empty by default, which is the state a deployment that authored none is in. */
  adoptions?: StorageAdoptionStatus[];
}

function renderSettings(handler: FetchHandler, options: RenderOptions = {}) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", "team-1");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const raw = typeof input === "string" ? input : input.toString();
    const path = new URL(raw, "http://test.local").pathname;
    if (path === "/api/users/me") return json(me(options.permissions ?? [TeamPermissions.StorageManage]));
    if (path === "/api/storage/routes/page") return json(page(options.routes ?? []));
    if (path === "/api/storage/data-classes") return json(routedDataClasses);
    if (path === "/api/storage/adoptions" && (init.method ?? "GET") === "GET") return json(options.adoptions ?? []);
    // Opening a profile asks what it still holds — the population its retirement guard counts. Answered here rather
    // than in every per-test handler; the drain's own behaviour is covered by StoragePlacementDrain.test.tsx.
    if (/^\/api\/storage\/profiles\/[^/]+\/placements\/totals$/.test(path)) return json([]);
    if (/^\/api\/storage\/profiles\/[^/]+\/placements$/.test(path)) return json(page([]));
    return handler(path, init);
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });

  return render(<QueryClientProvider client={client}><StorageSettings /></QueryClientProvider>);
}

type StepName = "credential" | "profile" | "route";

/** The rail's own state marker, read the way the page paints it. */
function stepState(step: StepName): string | null {
  return document.querySelector(`[data-step="${step}"]`)?.getAttribute("data-step-state") ?? null;
}

function railStates(): Record<StepName, string | null> {
  return { credential: stepState("credential"), profile: stepState("profile"), route: stepState("route") };
}

function activeStep(): string | null {
  return document.querySelector("[data-step-state='active']")?.getAttribute("data-step") ?? null;
}

/**
 * On screen once the destination's own card is. The NAME is no longer a unique signal — the card names the place and
 * the Advanced drawer's rows name it again — so the card's own menu, which only it has, is what is waited on.
 */
async function findDestination(name: string) {
  return await screen.findByRole("button", { name: `Actions for ${name}` });
}

/** A finished step collapses to its summary line; its rows are behind the disclosure. */
async function expandStep(title: string) {
  fireEvent.click(await screen.findByRole("button", { name: `Show ${title}` }));
}

function defaultHandler(options: { providers?: StorageProviderModuleSummary[]; profiles?: StorageProfileSummary[]; detail?: StorageProfileDetail; credentials?: StorageCredentialMetadata[] } = {}): FetchHandler {
  const providers = options.providers ?? [localProvider, secretProvider];
  const profiles = options.profiles ?? [profile];
  const profileDetail = options.detail ?? detail;
  return (path) => {
    if (path === "/api/storage/provider-modules") return json(providers);
    if (path === "/api/storage/credentials/page") return json(page(options.credentials ?? []));
    if (path === "/api/storage/profiles/page") return json(page(profiles));
    if (path === `/api/storage/profiles/${profile.id}`) return json(profileDetail);
    return json({ code: "not_found", message: `No stub for ${path}` }, 404);
  };
}

function setSchemaText(groupName: string, value: string) {
  const editor = within(screen.getByRole("group", { name: groupName })).getByRole("textbox");
  editor.innerHTML = value;
  fireEvent.input(editor);
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("storage profiles settings", () => {
  it("lists only safe credential metadata and never renders the opaque reference", async () => {
    renderSettings(defaultHandler({ credentials: [credential] }));

    await expandStep("Storage credentials");
    const list = await screen.findByRole("list", { name: "Storage credentials" });
    expect(within(list).getByText("aliyun-primary")).toBeInTheDocument();
    expect(within(list).getByText("Revision 3")).toBeInTheDocument();
    expect(within(list).getByText("AKID…7Q")).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(credential.credentialRef);
  });

  it("loads profile and credential pages without replacing already loaded rows", async () => {
    const secondProfile = { ...profile, id: "profile-2", stableName: "archive", providerTypeKey: secretProvider.typeKey };
    const secondCredential = { ...credential, id: "credential-2", stableName: "aliyun-archive", credentialRef: "db:00000000-0000-0000-0000-000000000456:3" };
    let profileReads = 0;
    let credentialReads = 0;
    renderSettings((path) => {
      if (path === "/api/storage/provider-modules") return json([localProvider, secretProvider]);
      if (path === "/api/storage/profiles/page") return json(profileReads++ === 0 ? page([profile], "profile-cursor") : page([secondProfile]));
      if (path === "/api/storage/credentials/page") return json(credentialReads++ === 0 ? page([credential], "credential-cursor") : page([secondCredential]));
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    await expandStep("Storage credentials");
    await screen.findByText("aliyun-primary");
    fireEvent.click(screen.getByRole("button", { name: "Load more profiles" }));
    fireEvent.click(screen.getByRole("button", { name: "Load more credentials" }));

    // Scoped to the paginated lists this test is about: a destination card names the same place, so an unscoped
    // query would pass on the card alone and prove nothing about the second page.
    const profileList = within(await screen.findByRole("list", { name: "Storage profiles" }));
    const credentialList = within(await screen.findByRole("list", { name: "Storage credentials" }));
    expect(await profileList.findByText("archive")).toBeInTheDocument();
    expect(await credentialList.findByText("aliyun-archive")).toBeInTheDocument();
    expect(profileList.getByText("primary")).toBeInTheDocument();
    expect(credentialList.getByText("aliyun-primary")).toBeInTheDocument();
    const requestUrls = vi.mocked(globalThis.fetch).mock.calls.map(([input]) => String(input));
    expect(requestUrls.some((url) => url.includes("/api/storage/profiles/page?limit=50&cursor=profile-cursor"))).toBe(true);
    expect(requestUrls.some((url) => url.includes("/api/storage/credentials/page?limit=50&cursor=credential-cursor"))).toBe(true);
  });

  it("creates a write-only credential from SecretSchema using password inputs", async () => {
    let submitted: Record<string, unknown> | undefined;
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([secretProvider]);
      if (path === "/api/storage/profiles/page") return json(page([]));
      if (path === "/api/storage/credentials/page" && method === "GET") return json(page([]));
      if (path === "/api/storage/credentials" && method === "POST") {
        submitted = JSON.parse(String(init.body)) as Record<string, unknown>;
        return json(credential);
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await screen.findByText("No storage credentials configured");
    fireEvent.click(screen.getByRole("button", { name: "Create storage credential" }));
    const dialog = await screen.findByRole("dialog", { name: "Create storage credential" });
    fireEvent.change(within(dialog).getByLabelText("Stable name"), { target: { value: "aliyun-primary" } });
    const secretInput = within(dialog).getByLabelText("Access key secret");
    expect(secretInput).toHaveAttribute("type", "password");
    expect(secretInput).toHaveAttribute("autocomplete", "new-password");
    fireEvent.change(secretInput, { target: { value: "super-secret-value" } });
    fireEvent.change(within(dialog).getByLabelText("Safe hint"), { target: { value: "AKID…7Q" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create credential" }));

    await waitFor(() => expect(submitted).toEqual({
      stableName: "aliyun-primary",
      providerTypeKey: secretProvider.typeKey,
      secret: { accessKeySecret: "super-secret-value" },
      safeHint: "AKID…7Q",
    }));
    expect(document.body).not.toHaveTextContent("super-secret-value");
    expect(document.body).not.toHaveTextContent(credential.credentialRef);
  });

  it("rotates with exact concurrency tokens and explicitly confirms terminal revocation", async () => {
    const requests: Array<{ path: string; body: Record<string, unknown> }> = [];
    let current = credential;
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([secretProvider]);
      if (path === "/api/storage/profiles/page") return json(page([]));
      if (path === "/api/storage/credentials/page" && method === "GET") return json(page([current]));
      if (path === `/api/storage/credentials/${credential.id}/revisions` && method === "POST") {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        requests.push({ path, body });
        current = { ...current, currentRevision: 4, xmin: 22, safeHint: "AKID…8R" };
        return json(current);
      }
      if (path === `/api/storage/credentials/${credential.id}/revoke` && method === "POST") {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        requests.push({ path, body });
        current = { ...current, state: "Revoked", xmin: 23, revokedDate: "2026-08-15T11:00:00Z" };
        return json(current);
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await expandStep("Storage credentials");
    await screen.findByText("aliyun-primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage credential aliyun-primary" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage storage credential aliyun-primary" });
    fireEvent.change(within(dialog).getByLabelText("Access key secret"), { target: { value: "rotated-secret" } });
    fireEvent.change(within(dialog).getByLabelText("Safe hint"), { target: { value: "AKID…8R" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Rotate credential" }));
    await waitFor(() => expect(requests[0]).toEqual({
      path: `/api/storage/credentials/${credential.id}/revisions`,
      body: { expectedXmin: 21, expectedCurrentRevision: 3, providerTypeKey: secretProvider.typeKey, secret: { accessKeySecret: "rotated-secret" }, safeHint: "AKID…8R" },
    }));

    const refreshedDialog = await screen.findByRole("dialog", { name: "Manage storage credential aliyun-primary" });
    fireEvent.click(within(refreshedDialog).getByRole("button", { name: "Revoke credential" }));
    const confirmation = await screen.findByRole("alertdialog", { name: "Revoke aliyun-primary?" });
    expect(confirmation).toHaveTextContent(/terminal/i);
    fireEvent.click(within(confirmation).getByRole("button", { name: "Revoke permanently" }));
    await waitFor(() => expect(requests[1]).toEqual({
      path: `/api/storage/credentials/${credential.id}/revoke`,
      body: { expectedXmin: 22, expectedCurrentRevision: 4 },
    }));
  });

  it("lists profile state, current revision, and installed provider while preserving the provider catalog", async () => {
    renderSettings(defaultHandler({ profiles: [{ ...profile, state: "Active" }] }));

    expect(screen.getByRole("heading", { name: "Artifact storage" })).toBeInTheDocument();
    // The header used to call the runtime "deployment-managed until qualification and cutover are complete",
    // which described a permanent layer as an unfinished one. An Active route over an Active profile IS where
    // the next write lands, so the header now says what this screen decides instead of disclaiming it.
    expect(screen.getByText(/Once a data route is Active, the next write for that data class lands on the profile it names/i)).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(/qualification and cutover/i);
    expect(document.body).not.toHaveTextContent(/control-plane configuration only/i);

    await expandStep("Storage profiles");
    const list = await screen.findByRole("list", { name: "Storage profiles" });
    expect(within(list).getByText("primary")).toBeInTheDocument();
    expect(within(list).getByText("Active")).toBeInTheDocument();
    expect(within(list).getByText("Revision 2")).toBeInTheDocument();
    expect(within(list).getByText("Local / shared filesystem")).toBeInTheDocument();

    expect(await screen.findByText("Aliyun OSS")).toBeInTheDocument();
    expect(screen.getByText(/Multipart upload/i)).toBeInTheDocument();
  });

  it("creates a Draft profile from non-secret SchemaForm values without sending secret fields or credentialRef", async () => {
    const requests: Array<{ path: string; method: string; body: Record<string, unknown> }> = [];
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([secretProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page" && method === "GET") return json(page([]));
      if (path === "/api/storage/profiles" && method === "POST") {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        requests.push({ path, method, body });
        return json({ ...detail, stableName: "archive", currentRevision: 1, xmin: 1, revisions: [] });
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await screen.findByText("No storage profiles configured");
    fireEvent.click(screen.getByRole("button", { name: "Create storage profile" }));

    const dialog = await screen.findByRole("dialog", { name: "Create storage profile" });
    fireEvent.change(within(dialog).getByLabelText("Stable name"), { target: { value: "archive" } });
    setSchemaText("Non-secret configuration", "artifact-bucket");
    expect(dialog).not.toHaveTextContent("Access key secret");
    expect(dialog).not.toHaveTextContent("accessKeySecret");

    fireEvent.click(within(dialog).getByRole("button", { name: "Create Draft" }));

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0]).toEqual({
      path: "/api/storage/profiles",
      method: "POST",
      body: { stableName: "archive", providerTypeKey: "aliyun-oss/v1", nonSecretConfig: { bucket: "artifact-bucket" } },
    });
    expect(JSON.stringify(requests[0].body)).not.toContain("credentialRef");
    expect(JSON.stringify(requests[0].body)).not.toContain("accessKeySecret");
  });

  it("appends a revision with the displayed xmin and currentRevision", async () => {
    let revisionPayload: Record<string, unknown> | undefined;
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page" && method === "GET") return json(page([profile]));
      if (path === `/api/storage/profiles/${profile.id}` && method === "GET") return json(detail);
      if (path === `/api/storage/profiles/${profile.id}/revisions` && method === "POST") {
        revisionPayload = JSON.parse(String(init.body)) as Record<string, unknown>;
        return json({
          ...detail,
          currentRevision: 3,
          xmin: 18,
          revisions: [{ ...detail.revisions[0], id: "revision-3", revision: 3, nonSecretConfig: { rootPath: "/artifacts/new" } }, ...detail.revisions],
        });
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    await screen.findByRole("dialog", { name: "Manage storage profile primary" });
    setSchemaText("Revision non-secret configuration", "/artifacts/new");
    fireEvent.click(screen.getByRole("button", { name: "Append revision" }));

    await waitFor(() => expect(revisionPayload).toEqual({
      expectedXmin: 17,
      expectedCurrentRevision: 2,
      providerTypeKey: "local-rwx/v1",
      nonSecretConfig: { rootPath: "/artifacts/new" },
    }));
    expect(await screen.findByText("Current revision 3")).toBeInTheDocument();
  });

  it("refetches stale data and gives an actionable message after a 409 conflict", async () => {
    let detailReads = 0;
    const latest = { ...detail, currentRevision: 3, xmin: 22, revisions: [{ ...detail.revisions[0], revision: 3 }] };
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page" && method === "GET") return json(page([profile]));
      if (path === `/api/storage/profiles/${profile.id}` && method === "GET") return json(detailReads++ === 0 ? detail : latest);
      if (path === `/api/storage/profiles/${profile.id}/state` && method === "PUT") {
        return json({ code: "storage_profile_conflict", message: "Storage profile version mismatch." }, 409);
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    await screen.findByText("Current revision 2");
    fireEvent.click(screen.getByRole("button", { name: "Set Active" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Storage profile version mismatch.");
    expect(screen.getByRole("alert")).toHaveTextContent(/latest data was reloaded/i);
    await waitFor(() => expect(detailReads).toBeGreaterThan(1));
    expect(await screen.findByText("Current revision 3")).toBeInTheDocument();
  });

  // Every 409 used to collapse into one "changed elsewhere" sentence. StorageProfileService throws several
  // distinct ones, and this is the case where the generic string is not merely vague but false: nothing
  // changed elsewhere, and the reason plus the exact fix were both in the message that got thrown away.
  it("surfaces the server's own 409 reason instead of assuming a concurrent edit", async () => {
    const refusal = "Storage profile cannot be retired while 2 active storage route(s) still target it. Repoint or disable those routes first.";
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page" && method === "GET") return json(page([activeProfile]));
      if (path === `/api/storage/profiles/${profile.id}` && method === "GET") return json({ ...detail, state: "Active" });
      if (path === `/api/storage/profiles/${profile.id}/state` && method === "PUT") return json({ code: "storage_profile_conflict", message: refusal }, 409);
      return json({ message: "Unexpected request" }, 500);
    });

    await expandStep("Storage profiles");
    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    await screen.findByText("Current revision 2");
    fireEvent.click(screen.getByRole("button", { name: "Retire profile" }));
    fireEvent.click(within(await screen.findByRole("alertdialog", { name: "Retire primary?" })).getByRole("button", { name: "Retire permanently" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(refusal);
    expect(screen.getByRole("alert")).not.toHaveTextContent(/changed elsewhere/i);
  });

  it("uses concurrency tokens for Active/Disabled/Retired state changes and explicitly confirms retirement", async () => {
    const states: Array<Record<string, unknown>> = [];
    let current = detail;
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page" && method === "GET") return json(page([{ ...profile, state: current.state, xmin: current.xmin }]));
      if (path === `/api/storage/profiles/${profile.id}` && method === "GET") return json(current);
      if (path === `/api/storage/profiles/${profile.id}/state` && method === "PUT") {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        states.push(body);
        current = { ...current, state: body.state as StorageProfileDetail["state"], xmin: current.xmin + 1 };
        return json(current);
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    await screen.findByText("Current revision 2");

    fireEvent.click(screen.getByRole("button", { name: "Set Active" }));
    await waitFor(() => expect(states).toHaveLength(1));
    fireEvent.click(screen.getByRole("button", { name: "Set Disabled" }));
    await waitFor(() => expect(states).toHaveLength(2));
    fireEvent.click(screen.getByRole("button", { name: "Retire profile" }));

    const confirmation = await screen.findByRole("alertdialog", { name: "Retire primary?" });
    expect(confirmation).toHaveTextContent(/terminal/i);
    expect(states).toHaveLength(2);
    fireEvent.click(within(confirmation).getByRole("button", { name: "Retire permanently" }));

    await waitFor(() => expect(states).toEqual([
      { expectedXmin: 17, expectedCurrentRevision: 2, state: "Active" },
      { expectedXmin: 18, expectedCurrentRevision: 2, state: "Disabled" },
      { expectedXmin: 19, expectedCurrentRevision: 2, state: "Retired" },
    ]));
    expect(within(screen.getByRole("dialog", { name: "Manage storage profile primary" })).getByText("Retired", { selector: ".cn-status" })).toBeInTheDocument();
  });

  it("never renders SecretSchema inputs or credentialRef and blocks activation when required credentials are absent", async () => {
    const secretDetail: StorageProfileDetail = {
      ...detail,
      currentRevision: 2,
      revisions: [
        { ...detail.revisions[0], revision: 2, providerTypeKey: secretProvider.typeKey, nonSecretConfig: { bucket: "archive" }, credentialRef: null },
        { ...detail.revisions[0], id: "revision-1", revision: 1, providerTypeKey: secretProvider.typeKey, nonSecretConfig: { bucket: "archive-old" }, credentialRef: "db:00000000-0000-0000-0000-000000000123:7" },
      ],
    };
    renderSettings(defaultHandler({ providers: [secretProvider], profiles: [{ ...profile, providerTypeKey: secretProvider.typeKey }], detail: secretDetail }));

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage storage profile primary" });

    expect(dialog).toHaveTextContent(/requires a Storage Credential before this profile can be activated/i);
    expect(within(dialog).getByRole("button", { name: "Set Active" })).toBeDisabled();
    expect(dialog).not.toHaveTextContent("accessKeySecret");
    expect(dialog).not.toHaveTextContent("Access key secret");
    expect(dialog).not.toHaveTextContent("db:00000000-0000-0000-0000-000000000123:7");
    expect(dialog).not.toHaveTextContent("credentialRef");
  });

  it("links a provider-matched credential by stable UI identity while keeping its opaque ref out of the DOM", async () => {
    let revisionPayload: Record<string, unknown> | undefined;
    const secretDetail: StorageProfileDetail = {
      ...detail,
      revisions: [{ ...detail.revisions[0], providerTypeKey: secretProvider.typeKey, nonSecretConfig: { bucket: "archive" }, credentialRef: null }],
    };
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([secretProvider]);
      if (path === "/api/storage/credentials/page") return json(page([credential]));
      if (path === "/api/storage/profiles/page" && method === "GET") return json(page([{ ...profile, providerTypeKey: secretProvider.typeKey }]));
      if (path === `/api/storage/profiles/${profile.id}` && method === "GET") return json(secretDetail);
      if (path === `/api/storage/profiles/${profile.id}/revisions` && method === "POST") {
        revisionPayload = JSON.parse(String(init.body)) as Record<string, unknown>;
        return json({ ...secretDetail, currentRevision: 3, xmin: 18, revisions: [{ ...secretDetail.revisions[0], revision: 3, credentialRef: credential.credentialRef }] });
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage storage profile primary" });
    fireEvent.change(within(dialog).getByLabelText("Storage credential"), { target: { value: credential.id } });
    expect(document.body).not.toHaveTextContent(credential.credentialRef);
    fireEvent.click(within(dialog).getByRole("button", { name: "Append revision" }));

    await waitFor(() => expect(revisionPayload).toEqual({
      expectedXmin: 17,
      expectedCurrentRevision: 2,
      providerTypeKey: secretProvider.typeKey,
      nonSecretConfig: { bucket: "archive" },
      credentialRef: credential.credentialRef,
    }));
    expect(document.body).not.toHaveTextContent(credential.credentialRef);
  });

  it("probes the current revision with write access by default and renders only the closed result vocabulary", async () => {
    let probeBody: Record<string, unknown> | undefined;
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page") return json(page([profile]));
      if (path === `/api/storage/profiles/${profile.id}` && method === "GET") return json(detail);
      if (path === `/api/storage/profiles/${profile.id}/probe` && method === "POST") {
        probeBody = JSON.parse(String(init.body)) as Record<string, unknown>;
        return json({ ...probeResult(), providerTypeKey: "provider-internal/raw", providerMessage: "secret-provider-text" });
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage storage profile primary" });
    expect(within(dialog).getByLabelText("Probe revision")).toHaveValue("current");
    expect(within(dialog).getByLabelText("Probe access")).toHaveValue("write");
    fireEvent.click(within(dialog).getByRole("button", { name: "Run write probe" }));

    await waitFor(() => expect(probeBody).toEqual({ profileRevision: null, verifyWriteAccess: true }));
    const result = await within(dialog).findByRole("status", { name: "Storage probe result" });
    expect(result).toHaveTextContent("Available");
    expect(result).toHaveTextContent("14 ms");
    expect(result).toHaveTextContent("Revision 2");
    expect(result).not.toHaveTextContent("provider-internal/raw");
    expect(result).not.toHaveTextContent("secret-provider-text");
    fireEvent.change(within(dialog).getByLabelText("Probe revision"), { target: { value: "2" } });
    expect(within(dialog).queryByRole("status", { name: "Storage probe result" })).not.toBeInTheDocument();
  });

  it("probes an exact revision read-only and clears the bound result before a profile mutation", async () => {
    const requests: Array<{ path: string; body: Record<string, unknown> }> = [];
    let current = detail;
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page") return json(page([{ ...profile, state: current.state, xmin: current.xmin }]));
      if (path === `/api/storage/profiles/${profile.id}` && method === "GET") return json(current);
      if (path === `/api/storage/profiles/${profile.id}/probe` && method === "POST") {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        requests.push({ path, body });
        return json(probeResult({
          writeAccessRequested: false,
          status: "Degraded",
          latencyMilliseconds: 91,
          failure: { stage: "Probe", code: "ProbeThrottled", retryable: true },
        }));
      }
      if (path === `/api/storage/profiles/${profile.id}/state` && method === "PUT") {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        requests.push({ path, body });
        current = { ...current, state: "Active", xmin: 18 };
        return json(current);
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage storage profile primary" });
    fireEvent.change(within(dialog).getByLabelText("Probe revision"), { target: { value: "2" } });
    fireEvent.change(within(dialog).getByLabelText("Probe access"), { target: { value: "read" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Run read probe" }));

    const result = await within(dialog).findByRole("status", { name: "Storage probe result" });
    expect(requests[0]).toEqual({ path: `/api/storage/profiles/${profile.id}/probe`, body: { profileRevision: 2, verifyWriteAccess: false } });
    expect(result).toHaveTextContent("Degraded");
    expect(result).toHaveTextContent("Probe");
    expect(result).toHaveTextContent("ProbeThrottled");
    expect(result).toHaveTextContent("Retryable");

    fireEvent.click(within(dialog).getByRole("button", { name: "Set Active" }));
    expect(within(dialog).queryByRole("status", { name: "Storage probe result" })).not.toBeInTheDocument();
    await waitFor(() => expect(requests).toHaveLength(2));
  });

  it("turns a safe signature reason into actionable guidance without rendering provider text", async () => {
    renderSettings(async (path, init) => {
      const method = init.method ?? "GET";
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page") return json(page([profile]));
      if (path === `/api/storage/profiles/${profile.id}` && method === "GET") return json(detail);
      if (path === `/api/storage/profiles/${profile.id}/probe` && method === "POST") return json({
        ...probeResult({ status: "Unavailable", failure: { stage: "Probe", code: "ProbeSignatureMismatch", retryable: false } }),
        providerMessage: "must never render",
      });
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage storage profile primary" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Run write probe" }));

    const result = await within(dialog).findByRole("status", { name: "Storage probe result" });
    expect(result).toHaveTextContent("ProbeSignatureMismatch");
    expect(result).toHaveTextContent("an endpoint and region that don't match each other");
    expect(result).not.toHaveTextContent("must never render");
  });

  it("prevents duplicate probes and aborts the in-flight request when the profile editor unmounts", async () => {
    let probeCalls = 0;
    const captured: { signal: AbortSignal | null } = { signal: null };
    const never = new Promise<Response>(() => {});
    const rendered = renderSettings((path, init) => {
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page") return json(page([profile]));
      if (path === `/api/storage/profiles/${profile.id}` && (init.method ?? "GET") === "GET") return json(detail);
      if (path === `/api/storage/profiles/${profile.id}/probe`) {
        probeCalls += 1;
        captured.signal = init.signal ?? null;
        return never;
      }
      return json({ message: "Unexpected request" }, 500);
    });

    await findDestination("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage storage profile primary" });
    const run = within(dialog).getByRole("button", { name: "Run write probe" });
    fireEvent.click(run);
    fireEvent.click(run);

    expect(await within(dialog).findByRole("button", { name: "Probing…" })).toBeDisabled();
    expect(probeCalls).toBe(1);
    expect(captured.signal).toBeInstanceOf(AbortSignal);
    expect(captured.signal?.aborted).toBe(false);
    rendered.unmount();
    expect(captured.signal?.aborted).toBe(true);
  });

  it("distinguishes empty profiles from profile API failures", async () => {
    const { rerender } = renderSettings((path) => {
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page") return json(page([]));
      return json({}, 404);
    });
    expect(await screen.findByText("No storage profiles configured")).toBeInTheDocument();

    rerender(<div />);
    vi.unstubAllGlobals();
    renderSettings((path) => {
      if (path === "/api/storage/provider-modules") return json([localProvider]);
      if (path === "/api/storage/credentials/page") return json(page([]));
      if (path === "/api/storage/profiles/page") return json({ code: "storage_unavailable", message: "Profile ledger unavailable" }, 503);
      return json({}, 404);
    });
    expect(await screen.findByText("Couldn't load storage profiles")).toBeInTheDocument();
    expect(screen.getByText("Profile ledger unavailable")).toBeInTheDocument();
    expect(screen.queryByText("No storage profiles configured")).not.toBeInTheDocument();
  });
});

const activeProfile: StorageProfileSummary = { ...profile, state: "Active" };

function routeSummary(state: StorageRouteSummary["state"]): StorageRouteSummary {
  return {
    id: "route-1", dataClassTypeKey: "workflow-artifact/v1", state, currentRevision: 1, xmin: 30,
    storageProfileId: activeProfile.id, storageProfileStableName: activeProfile.stableName,
    profileRevisionMode: "CurrentAtWrite", pinnedProfileRevision: null,
    createdDate: "2026-08-14T10:00:00Z", lastModifiedDate: "2026-08-15T10:00:00Z",
  };
}

describe("storage guided flow", () => {
  // The chain is enforced by the server, not by this screen: a route may only target an Active profile
  // (StorageRouteService.RequireActiveProfileAsync), and a profile whose provider declares required secret
  // inputs may only be activated once a credential is linked. The rail has to name the same order.
  const completionStates: Array<{ name: string; credentials: StorageCredentialMetadata[]; profiles: StorageProfileSummary[]; routes: StorageRouteSummary[]; expected: Record<StepName, string | null> }> = [
    { name: "nothing configured", credentials: [], profiles: [], routes: [], expected: { credential: "active", profile: "upcoming", route: "locked" } },
    { name: "credential only", credentials: [credential], profiles: [], routes: [], expected: { credential: "done", profile: "active", route: "locked" } },
    { name: "credential plus a Draft profile", credentials: [credential], profiles: [profile], routes: [], expected: { credential: "done", profile: "active", route: "locked" } },
    { name: "credential plus an Active profile", credentials: [credential], profiles: [activeProfile], routes: [], expected: { credential: "done", profile: "done", route: "active" } },
    // A later step being reachable does not move the accent onto it: local-rwx needs no secret, so this
    // team has an Active profile with the credential step still untouched. Routing stays available —
    // upcoming, not locked — because nothing refuses it.
    { name: "an Active profile reached without a credential", credentials: [], profiles: [activeProfile], routes: [], expected: { credential: "active", profile: "done", route: "upcoming" } },
    { name: "a Draft route over an Active profile", credentials: [credential], profiles: [activeProfile], routes: [routeSummary("Draft")], expected: { credential: "done", profile: "done", route: "active" } },
  ];

  it.each(completionStates)("makes the first incomplete step the active one — $name", async ({ credentials, profiles, routes, expected }) => {
    renderSettings(defaultHandler({ credentials, profiles }), { routes });

    await waitFor(() => expect(railStates()).toEqual(expected));
    expect(activeStep()).toBe(Object.entries(expected).find(([, value]) => value === "active")?.[0]);
  });

  it("leaves no step active once every step is complete", async () => {
    renderSettings(defaultHandler({ credentials: [credential], profiles: [activeProfile] }), { routes: [routeSummary("Active")] });

    await waitFor(() => expect(stepState("route")).toBe("done"));
    expect(activeStep()).toBeNull();
    expect(stepState("credential")).toBe("done");
    expect(stepState("profile")).toBe("done");
  });

  it("shows exactly one primary action on the whole screen", async () => {
    renderSettings(defaultHandler({ credentials: [credential], profiles: [] }));

    await waitFor(() => expect(railStates()).toEqual({ credential: "done", profile: "active", route: "locked" }));
    expect(document.querySelectorAll(".btn-primary")).toHaveLength(1);
  });

  it("states the route step's precondition inline instead of offering a disabled button beside a separate hint", async () => {
    renderSettings(defaultHandler({ credentials: [credential], profiles: [profile] }));

    await waitFor(() => expect(stepState("route")).toBe("locked"));
    expect(screen.getByText(/Available once a storage profile is Active/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Create data route" })).not.toBeInTheDocument();
    expect(screen.queryByText(/Activate a storage profile before creating or revising a data route/i)).not.toBeInTheDocument();
  });

  // local-rwx/v1 declares no secret properties at all, so a Storage Credential could hold nothing for it.
  it("omits the credential step when no installed provider takes a secret", async () => {
    renderSettings(defaultHandler({ providers: [localProvider], profiles: [] }));

    await waitFor(() => expect(activeStep()).toBe("profile"));
    expect(stepState("credential")).toBeNull();
    expect(screen.queryByRole("heading", { name: "Storage credentials" })).not.toBeInTheDocument();
  });

  it("presents the credential step when an installed provider takes a secret", async () => {
    renderSettings(defaultHandler({ providers: [secretProvider], profiles: [] }));

    await waitFor(() => expect(activeStep()).toBe("credential"));
    expect(screen.getByRole("heading", { name: "Storage credentials" })).toBeInTheDocument();
  });

  // Same rule the roster follows: a control the caller may not use is ABSENT, not present-and-refusing.
  it("renders no write control without storage.manage", async () => {
    // A Draft profile is the state that offers the most write controls to a caller who does hold it:
    // Activate, Create, and a per-row Manage. None of them may appear here.
    renderSettings(defaultHandler({ credentials: [credential], profiles: [profile] }), { permissions: [] });

    expect(await screen.findByText("Lands here")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Create /i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Manage /i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Activate /i })).not.toBeInTheDocument();
    expect(screen.getByText(/needs the storage.manage permission/i)).toBeInTheDocument();
  });

  it("names the deployment catalog as deployment-set rather than as a fourth thing to configure", async () => {
    renderSettings(defaultHandler({ profiles: [] }));

    const catalog = await screen.findByRole("region", { name: "Installed providers" });
    expect(catalog).toHaveAttribute("data-scope", "deployment");
    expect(catalog).toHaveTextContent(/set by this deployment/i);
    expect(within(catalog).queryByRole("button")).not.toBeInTheDocument();
  });
});
