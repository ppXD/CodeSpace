import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { StorageProviderModuleSummary } from "@/api/storage";
import type { StorageDefaultSummary } from "@/api/storageDefaults";

import { StorageDefaultsAdmin } from "./StorageDefaultsAdmin";

const ossProvider: StorageProviderModuleSummary = {
  typeKey: "aliyun-oss/v1",
  displayName: "Aliyun OSS",
  configSchema: {
    type: "object",
    properties: {
      endpoint: { type: "string", title: "Endpoint" },
      bucket: { type: "string", title: "Bucket name" },
      keyPrefix: { type: "string", title: "Key prefix" },
    },
    required: ["endpoint", "bucket", "keyPrefix"],
    additionalProperties: false,
  },
  secretSchema: { type: "object", properties: {}, additionalProperties: false },
  capabilities: [],
  teamNamespaceProperty: "keyPrefix",
};

/** A provider that cannot give each team a namespace of its own, so it can never be a deployment default. */
const undividable: StorageProviderModuleSummary = {
  typeKey: "undividable/v1",
  displayName: "Undividable store",
  configSchema: { type: "object", properties: { url: { type: "string", title: "URL" } }, additionalProperties: false },
  secretSchema: { type: "object", properties: {}, additionalProperties: false },
  capabilities: [],
  teamNamespaceProperty: null,
};

const authored: StorageDefaultSummary = {
  id: "default-1",
  dataClassTypeKey: "agent-run-log/v1",
  revision: 2,
  providerTypeKey: "aliyun-oss/v1",
  adoptionPolicy: "Automatic",
  isEnabled: true,
  hasCredential: true,
  credentialSafeHint: "AK…1234",
  xmin: 77,
  createdDate: "2026-08-01T00:00:00Z",
  lastModifiedDate: "2026-08-02T00:00:00Z",
};

const dataClasses = [
  { typeKey: "agent-run-log/v1", displayName: "Agent run logs" },
  { typeKey: "workflow-artifact/v1", displayName: "Workflow artifacts" },
];

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

interface Options {
  defaults?: StorageDefaultSummary[];
  providers?: StorageProviderModuleSummary[];
  listStatus?: number;
  onPost?: (body: unknown) => Response;
  onPut?: (path: string, body: unknown) => Response;
}

function renderAdmin(options: Options = {}) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
    const method = init.method ?? "GET";
    if (path === "/api/admin/storage-defaults/provider-modules") return json(options.providers ?? [ossProvider]);
    if (path === "/api/admin/storage-defaults/data-classes") return json(dataClasses);
    if (path === "/api/admin/storage-defaults" && method === "GET") {
      return options.listStatus != null ? json({ code: "forbidden", message: "no" }, options.listStatus) : json(options.defaults ?? []);
    }
    if (path === "/api/admin/storage-defaults" && method === "POST") return options.onPost?.(JSON.parse(String(init.body))) ?? json({});
    if (method === "PUT") return options.onPut?.(path, JSON.parse(String(init.body))) ?? json({});
    return json([]);
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });

  return render(<QueryClientProvider client={client}><StorageDefaultsAdmin /></QueryClientProvider>);
}


afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("StorageDefaultsAdmin", () => {
  it("refuses to guess for someone without the capability", async () => {
    renderAdmin({ listStatus: 403 });

    expect(await screen.findByText("Not yours to see")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "New default" })).not.toBeInTheDocument();
  });

  it("does not offer the property that IS the namespace", async () => {
    // The server refuses a template config that sets it — a template describes the whole deployment while
    // that property names one team — so offering the field would be offering a rejection. It is asked for
    // separately, as a root, which is the shape the server accepts.
    renderAdmin();

    fireEvent.click(await screen.findByRole("button", { name: "New default" }));
    const form = await screen.findByRole("group", { name: "New storage default" });
    await within(form).findByRole("option", { name: "Aliyun OSS" });
    fireEvent.change(within(form).getByLabelText("Where it goes"), { target: { value: "aliyun-oss/v1" } });

    expect(await within(form).findByText("Bucket name")).toBeInTheDocument();
    // The server refuses a template that sets it, so offering the field would be offering a rejection.
    expect(within(form).queryByText("Key prefix")).not.toBeInTheDocument();
    expect(within(form).getByLabelText("Namespace root")).toBeInTheDocument();
  });

  it("does not offer a provider that cannot give each team its own namespace", async () => {
    // Every team materialized from such a provider would share one namespace, and identical bytes in two
    // teams would then be one object. The server refuses it; offering it here would only produce that error.
    renderAdmin({ providers: [ossProvider, undividable] });

    fireEvent.click(await screen.findByRole("button", { name: "New default" }));
    const select = within(await screen.findByRole("group", { name: "New storage default" })).getByLabelText("Where it goes");

    expect(within(select).getByRole("option", { name: "Aliyun OSS" })).toBeInTheDocument();
    expect(within(select).queryByRole("option", { name: "Undividable store" })).not.toBeInTheDocument();
    expect(screen.getByText(/cannot give each team a namespace of its own are not offered/i)).toBeInTheDocument();
  });

  it("authors a default from the class, the provider config and the root", async () => {
    let posted: Record<string, unknown> | undefined;
    renderAdmin({ onPost: (body) => { posted = body as Record<string, unknown>; return json({ ...authored, nonSecretConfig: {}, namespaceRoot: "codespace" }); } });

    fireEvent.click(await screen.findByRole("button", { name: "New default" }));
    const form = await screen.findByRole("group", { name: "New storage default" });
    await within(form).findByRole("option", { name: "Aliyun OSS" });
    fireEvent.change(within(form).getByLabelText("Where it goes"), { target: { value: "aliyun-oss/v1" } });
    await within(form).findByText("Bucket name");
    fireEvent.change(within(form).getByLabelText("Namespace root"), { target: { value: "codespace" } });
    fireEvent.click(within(form).getByRole("button", { name: "Author this default" }));

    await waitFor(() => expect(posted).toBeDefined());
    expect(posted).toMatchObject({
      dataClassTypeKey: "agent-run-log/v1",
      providerTypeKey: "aliyun-oss/v1",
      namespaceRoot: "codespace",
      adoptionPolicy: "Explicit",
      isEnabled: true,
    });
    // The namespace reaches the server as a ROOT, never as the provider's own field: the server appends
    // the per-team segment, and a config that named it would hand every team one shared namespace.
    expect((posted!.nonSecretConfig as Record<string, unknown>).keyPrefix).toBeUndefined();
  });

  it("carries the row's own concurrency token when switching one off", async () => {
    // Two operators editing one template must not silently overwrite each other, and the server can only
    // refuse the second if the first's token is what it was given.
    let put: { path: string; body: Record<string, unknown> } | undefined;
    renderAdmin({ defaults: [authored], onPut: (path, body) => { put = { path, body: body as Record<string, unknown> }; return json(authored); } });

    fireEvent.click(await screen.findByRole("button", { name: "Switch off" }));

    await waitFor(() => expect(put).toBeDefined());
    expect(put!.path).toBe("/api/admin/storage-defaults/default-1/enabled");
    expect(put!.body).toEqual({ expectedXmin: 77, expectedRevision: 2, isEnabled: false });
  });

  it("says a switched-off default leaves the teams already on it alone", async () => {
    renderAdmin({ defaults: [{ ...authored, isEnabled: false }] });

    expect(await screen.findByText(/Teams already on it are unaffected/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Offer again" })).toBeInTheDocument();
  });
});
