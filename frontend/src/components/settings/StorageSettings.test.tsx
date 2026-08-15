import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { StorageCredentialMetadata, StorageProfileDetail, StorageProfileSummary, StorageProviderModuleSummary } from "@/api/storage";

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

function renderSettings(handler: FetchHandler) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", "team-1");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const raw = typeof input === "string" ? input : input.toString();
    return handler(new URL(raw, "http://test.local").pathname, init);
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });

  return render(<QueryClientProvider client={client}><StorageSettings /></QueryClientProvider>);
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

    await screen.findByText("primary");
    await screen.findByText("aliyun-primary");
    fireEvent.click(screen.getByRole("button", { name: "Load more profiles" }));
    fireEvent.click(screen.getByRole("button", { name: "Load more credentials" }));

    expect(await screen.findByText("archive")).toBeInTheDocument();
    expect(await screen.findByText("aliyun-archive")).toBeInTheDocument();
    expect(screen.getByText("primary")).toBeInTheDocument();
    expect(screen.getByText("aliyun-primary")).toBeInTheDocument();
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
    expect(screen.getByText(/Active profiles are control-plane configuration only/i)).toBeInTheDocument();
    expect(screen.getByText(/deployment-managed until qualification and cutover/i)).toBeInTheDocument();

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

    await screen.findByText("primary");
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

    await screen.findByText("primary");
    fireEvent.click(screen.getByRole("button", { name: "Manage primary" }));
    await screen.findByText("Current revision 2");
    fireEvent.click(screen.getByRole("button", { name: "Set Active" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/changed elsewhere/i);
    expect(screen.getByRole("alert")).toHaveTextContent(/review the latest revision and try again/i);
    await waitFor(() => expect(detailReads).toBeGreaterThan(1));
    expect(await screen.findByText("Current revision 3")).toBeInTheDocument();
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

    await screen.findByText("primary");
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

    await screen.findByText("primary");
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

    await screen.findByText("primary");
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
